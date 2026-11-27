using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;

namespace Clinic.Client.Core.Clinical;

public sealed class InventoryException(string message) : Exception(message);

public sealed record ItemStock(LocalInventoryItem Item, decimal OnHand, IReadOnlyList<BatchStock> Batches, IReadOnlyList<StockAlert> Alerts);

/// <summary>A prescription line waiting at the pharmacy, with the item that dispenses it and the quantity suggested.</summary>
public sealed record DispenseLine(LocalPrescriptionItem RxItem, LocalInventoryItem? Item, decimal Suggested, decimal OnHand, bool AlreadyDispensed);

/// <summary>
/// §4.7 on the device (FR-INV-01…03): receive stock in batches, dispense prescriptions first-expiry-first-out, use
/// consumables, adjust, write off expired stock, transfer between branches, and raise low-stock and expiry alerts.
/// Every change is a movement row (FR-INV-02), so offline devices merge without overwriting each other.
/// </summary>
public sealed class InventoryService(LocalStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private DateTime Today => _clock.GetLocalNow().Date;
    private Guid UserId => store.Context.UserId;

    public async Task<List<LocalInventoryItem>> ItemsAsync(string? search = null, string? category = null)
    {
        var q = search?.Trim() ?? "";
        return (await store.ListAsync<LocalInventoryItem>(i => i.IsActive))
            .Where(i => (category is null || i.Category == category) && (q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name).ToList();
    }

    // ------------------------------------------------------------------ stock levels and alerts

    public async Task<List<BatchStock>> BatchesAsync(Guid itemId, Guid branchId)
    {
        var batches = await store.ListAsync<LocalStockBatch>(b => b.ItemId == itemId && b.BranchId == branchId);
        var moves = (await store.ListAsync<LocalStockMovement>(m => m.ItemId == itemId && m.BranchId == branchId)).ToLookup(m => m.BatchId);
        return batches.Select(b => new BatchStock(b.Id, b.BatchNo, b.Expiry, moves[b.Id].Sum(m => m.Quantity))).OrderBy(b => b.Expiry ?? DateTime.MaxValue).ToList();
    }

    public async Task<List<ItemStock>> StockAsync(Guid branchId, bool onlyWithAlerts = false)
    {
        var items = (await store.ListAsync<LocalInventoryItem>(i => i.IsActive)).ToDictionary(i => i.Id);
        var batches = (await store.ListAsync<LocalStockBatch>(b => b.BranchId == branchId)).ToLookup(b => b.ItemId);
        var moves = (await store.ListAsync<LocalStockMovement>(m => m.BranchId == branchId)).ToLookup(m => m.BatchId);
        var result = new List<ItemStock>();
        foreach (var item in items.Values.OrderBy(i => i.Name))
        {
            var bs = batches[item.Id].Select(b => new BatchStock(b.Id, b.BatchNo, b.Expiry, moves[b.Id].Sum(m => m.Quantity))).OrderBy(b => b.Expiry ?? DateTime.MaxValue).ToList();
            // Items never stocked at this branch are not "low": the branch simply does not carry them (same rule as the server).
            var alerts = bs.Count == 0 ? [] : Stock.Alerts(item.Id, item.Name, item.ReorderLevel, bs, Today);
            if (onlyWithAlerts && alerts.Count == 0) continue;
            result.Add(new ItemStock(item, bs.Sum(b => b.OnHand), bs, alerts));
        }
        return result;
    }

    public async Task<List<StockAlert>> AlertsAsync(Guid branchId) => (await StockAsync(branchId, onlyWithAlerts: true)).SelectMany(s => s.Alerts).ToList();

    // ------------------------------------------------------------------ stock in

    /// <summary>FR-INV-01 purchase: a new batch at the branch and its inbound movement, in one transaction.</summary>
    public async Task<LocalStockBatch> ReceiveAsync(Guid itemId, Guid branchId, string batchNo, decimal quantity, DateTime? expiry, decimal unitCost,
        string? supplier = null, string? reference = null)
    {
        var item = await store.GetAsync<LocalInventoryItem>(itemId) ?? throw new InventoryException("Item not found.");
        if (quantity <= 0) throw new InventoryException("Enter the quantity received.");
        if (string.IsNullOrWhiteSpace(batchNo)) throw new InventoryException("Enter the batch number.");
        if (expiry is { } e && e.Date < Today) throw new InventoryException("That batch has already expired.");
        var batch = new LocalStockBatch
        {
            Id = Guid.NewGuid(), ItemId = item.Id, BranchId = branchId, BatchNo = batchNo.Trim(), Expiry = expiry?.Date, UnitCost = unitCost,
            Supplier = supplier, ReceivedAt = UtcNow,
        };
        await store.WriteAsync(b =>
        {
            b.Insert(batch);
            b.Insert(Movement(item.Id, batch.Id, branchId, MovementKind.Purchase, quantity, reference, null, null));
        });
        return batch;
    }

    // ------------------------------------------------------------------ dispensing (FR-INV-01, FR-BIL-02)

    /// <summary>The encounter's prescription lines at the pharmacy: the matching item (through the drug master) and the suggested quantity.</summary>
    public async Task<List<DispenseLine>> PrescriptionAsync(Guid encounterId, Guid branchId)
    {
        var rx = (await store.ListAsync<LocalPrescription>(p => p.EncounterId == encounterId)).FirstOrDefault();
        if (rx is null) return [];
        var items = await store.ListAsync<LocalInventoryItem>(i => i.IsActive);
        var dispensed = (await store.ListAsync<LocalDispensation>(d => d.PatientId == rx.PatientId)).Where(d => d.PrescriptionItemId is not null)
            .Select(d => d.PrescriptionItemId!.Value).ToHashSet();
        var result = new List<DispenseLine>();
        foreach (var line in (await store.ListAsync<LocalPrescriptionItem>(i => i.PrescriptionId == rx.Id)).OrderBy(i => i.SortOrder))
        {
            var item = items.FirstOrDefault(i => line.DrugId is not null && i.DrugId == line.DrugId)
                       ?? items.FirstOrDefault(i => i.Name.StartsWith(line.DrugName, StringComparison.OrdinalIgnoreCase));
            var onHand = item is null ? 0 : (await BatchesAsync(item.Id, branchId)).Sum(b => b.OnHand);
            result.Add(new DispenseLine(line, item, Stock.QuantityFor(line.Dose, line.Frequency, line.DurationDays), onHand, dispensed.Contains(line.Id)));
        }
        return result;
    }

    /// <summary>
    /// Hands out medicine: one dispensation (the billable record) and FEFO movements from the batches. When the device
    /// knows of too little stock it refuses, unless <paramref name="allowShort"/> (stock not yet entered) — the shortfall
    /// is then taken from the newest batch and shows as negative stock to be counted and adjusted.
    /// </summary>
    public async Task<LocalDispensation> DispenseAsync(Guid patientId, Guid branchId, Guid itemId, decimal quantity, Guid? prescriptionItemId = null,
        decimal? unitPrice = null, bool allowShort = false, string? notes = null)
    {
        var item = await store.GetAsync<LocalInventoryItem>(itemId) ?? throw new InventoryException("Item not found.");
        _ = await store.GetAsync<LocalPatient>(patientId) ?? throw new InventoryException("Patient not found.");
        if (quantity <= 0) throw new InventoryException("Enter a quantity.");
        if (prescriptionItemId is { } pid && (await store.ListAsync<LocalDispensation>(d => d.PrescriptionItemId == pid)).Count > 0)
            throw new InventoryException("This prescription line has already been dispensed.");

        var batches = await BatchesAsync(itemId, branchId);
        var (picks, shortBy) = Stock.PickFefo(batches, quantity, Today);
        if (shortBy > 0)
        {
            if (!allowShort) throw new InventoryException($"Only {quantity - shortBy:0.##} {item.Unit}(s) of {item.Name} in stock.");
            var fallback = batches.OrderByDescending(b => b.Expiry ?? DateTime.MaxValue).FirstOrDefault()
                           ?? throw new InventoryException($"{item.Name} has never been received at this branch.");
            picks = [.. picks, new BatchPick(fallback.BatchId, fallback.BatchNo, fallback.Expiry, shortBy)];
        }

        var d = new LocalDispensation
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, ItemId = item.Id, ItemName = item.Name, PrescriptionItemId = prescriptionItemId,
            Quantity = quantity, UnitPrice = unitPrice ?? item.SalePrice, DispensedAt = UtcNow, DispensedBy = UserId, Notes = notes,
        };
        await store.WriteAsync(b =>
        {
            b.Insert(d); // before its movements: the server checks that a dispensing movement belongs to a dispensation
            foreach (var p in picks) b.Insert(Movement(item.Id, p.BatchId, branchId, MovementKind.Dispense, -p.Quantity, null, null, d.Id));
        });
        return d;
    }

    /// <summary>Medicine returned unopened: back into the batch it came from (most recent dispensing movement).</summary>
    public async Task ReturnAsync(Guid dispensationId, decimal quantity, string reason)
    {
        var d = await store.GetAsync<LocalDispensation>(dispensationId) ?? throw new InventoryException("Dispensation not found.");
        if (quantity <= 0 || quantity > d.Quantity) throw new InventoryException($"Return between 1 and {d.Quantity:0.##}.");
        var from = (await store.ListAsync<LocalStockMovement>(m => m.SourceId == d.Id)).OrderByDescending(m => m.MovedAt).First();
        await store.InsertAsync(Movement(d.ItemId, from.BatchId, d.BranchId, MovementKind.Return, quantity, null, reason, d.Id));
    }

    // ------------------------------------------------------------------ consumables, adjustments, expiry, transfers

    /// <summary>Consumables of a finished procedure (FR-PRC-01), matched to items by name; unknown ones are skipped and returned.</summary>
    public async Task<IReadOnlyList<string>> ConsumeForProcedureAsync(Guid procedureId)
    {
        var p = await store.GetAsync<LocalProcedure>(procedureId) ?? throw new InventoryException("Procedure not found.");
        if ((await store.ListAsync<LocalStockMovement>(m => m.SourceId == procedureId)).Count > 0) return [];
        var items = await store.ListAsync<LocalInventoryItem>(i => i.IsActive);
        var skipped = new List<string>();
        var moves = new List<LocalStockMovement>();
        foreach (var c in ProcedureService.Consumables(p.ConsumablesJson))
        {
            var item = items.FirstOrDefault(i => string.Equals(i.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            // Liquids measured in ml are used from an opened vial, not counted out of stock per procedure.
            if (item is null || c.Unit == "ml") { skipped.Add(c.Name); continue; }
            var (picks, shortBy) = Stock.PickFefo(await BatchesAsync(item.Id, p.BranchId), c.Quantity, Today);
            if (shortBy > 0) { skipped.Add($"{c.Name} (short by {shortBy:0.##})"); continue; }
            moves.AddRange(picks.Select(x => Movement(item.Id, x.BatchId, p.BranchId, MovementKind.Consume, -x.Quantity, null, p.Name, procedureId)));
        }
        if (moves.Count > 0) await store.WriteAsync(b => { foreach (var m in moves) b.Insert(m); });
        return skipped;
    }

    /// <summary>Stock count: records the difference between counted and recorded as an adjustment.</summary>
    public async Task<LocalStockMovement?> CountAsync(Guid batchId, decimal counted, string reason)
    {
        var batch = await store.GetAsync<LocalStockBatch>(batchId) ?? throw new InventoryException("Batch not found.");
        if (counted < 0) throw new InventoryException("A count cannot be negative.");
        if (string.IsNullOrWhiteSpace(reason)) throw new InventoryException("Give a reason for the adjustment.");
        var onHand = (await store.ListAsync<LocalStockMovement>(m => m.BatchId == batchId)).Sum(m => m.Quantity);
        var diff = counted - onHand;
        if (diff == 0) return null;
        return await store.InsertAsync(Movement(batch.ItemId, batch.Id, batch.BranchId, MovementKind.Adjust, diff, null, reason.Trim(), null));
    }

    /// <summary>FR-INV-03: write off what is left of an expired (or damaged) batch.</summary>
    public async Task<LocalStockMovement?> WriteOffAsync(Guid batchId, string reason = "Expired")
    {
        var batch = await store.GetAsync<LocalStockBatch>(batchId) ?? throw new InventoryException("Batch not found.");
        var onHand = (await store.ListAsync<LocalStockMovement>(m => m.BatchId == batchId)).Sum(m => m.Quantity);
        if (onHand <= 0) return null;
        return await store.InsertAsync(Movement(batch.ItemId, batch.Id, batch.BranchId, MovementKind.Expired, -onHand, null, reason, null));
    }

    /// <summary>Moves stock to another branch: out of this batch, into a matching batch there (same number and expiry).</summary>
    public async Task<LocalStockBatch> TransferAsync(Guid batchId, Guid toBranchId, decimal quantity)
    {
        var batch = await store.GetAsync<LocalStockBatch>(batchId) ?? throw new InventoryException("Batch not found.");
        if (toBranchId == batch.BranchId) throw new InventoryException("Choose another branch.");
        var onHand = (await store.ListAsync<LocalStockMovement>(m => m.BatchId == batchId)).Sum(m => m.Quantity);
        if (quantity <= 0 || quantity > onHand) throw new InventoryException($"Transfer between 1 and {onHand:0.##}.");
        var target = (await store.ListAsync<LocalStockBatch>(b => b.ItemId == batch.ItemId && b.BranchId == toBranchId && b.BatchNo == batch.BatchNo)).FirstOrDefault();
        var transferId = Guid.NewGuid();
        await store.WriteAsync(b =>
        {
            if (target is null)
            {
                target = new LocalStockBatch
                {
                    Id = Guid.NewGuid(), ItemId = batch.ItemId, BranchId = toBranchId, BatchNo = batch.BatchNo, Expiry = batch.Expiry, UnitCost = batch.UnitCost,
                    Supplier = batch.Supplier, ReceivedAt = UtcNow,
                };
                b.Insert(target);
            }
            b.Insert(Movement(batch.ItemId, batch.Id, batch.BranchId, MovementKind.TransferOut, -quantity, null, "Transfer", transferId));
            b.Insert(Movement(batch.ItemId, target.Id, toBranchId, MovementKind.TransferIn, quantity, null, "Transfer", transferId));
        });
        return target!;
    }

    public async Task<List<LocalStockMovement>> LedgerAsync(Guid itemId, Guid branchId) =>
        (await store.ListAsync<LocalStockMovement>(m => m.ItemId == itemId && m.BranchId == branchId)).OrderByDescending(m => m.MovedAt).ToList();

    private LocalStockMovement Movement(Guid itemId, Guid batchId, Guid branchId, string kind, decimal quantity, string? reference, string? reason, Guid? sourceId) => new()
    {
        Id = Guid.NewGuid(), ItemId = itemId, BatchId = batchId, BranchId = branchId, Kind = kind, Quantity = quantity, MovedAt = UtcNow, MovedBy = UserId,
        Reference = reference, Reason = reason, SourceId = sourceId,
    };
}
