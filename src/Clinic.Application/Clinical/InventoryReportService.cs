using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Application.Forms;
using Clinic.Domain.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Reports;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>FR-INV-01 item master (admin, online) and the server side of the reports (FR-REP-01…03).</summary>
public sealed class InventoryReportService(IAppDbContext db)
{
    // ---------------------------------------------------------------- items

    public async Task<IReadOnlyList<InventoryItemDto>> ItemsAsync(CancellationToken ct) =>
        (await db.InventoryItems.OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<InventoryItemDto> SaveItemAsync(Guid? id, SaveInventoryItemRequest r, CancellationToken ct)
    {
        var code = r.Code?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is 0 or > 40) throw new RequestValidationException("code", "Code is required (max 40).");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) throw new RequestValidationException("name", "Name is required (max 200).");
        if (!ItemCategory.All.Contains(r.Category)) throw new RequestValidationException("category", $"Unknown category '{r.Category}'.");
        if (r.ReorderLevel < 0 || r.SalePrice < 0) throw new RequestValidationException("salePrice", "Prices and levels cannot be negative.");
        if (r.DrugId is { } d && !await db.Drugs.AnyAsync(x => x.Id == d, ct)) throw new RequestValidationException("drugId", "Drug not found.");
        if (await db.InventoryItems.AnyAsync(i => i.Code == code && i.Id != id, ct)) throw new RequestValidationException("code", "Another item uses this code.");
        InventoryItem item;
        if (id is { } iid) item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == iid, ct) ?? throw new NotFoundException("Item", iid);
        else { item = new InventoryItem { Id = Guid.CreateVersion7() }; db.InventoryItems.Add(item); }
        item.Code = code;
        item.Name = r.Name.Trim();
        item.Category = r.Category;
        item.DrugId = r.DrugId;
        item.Unit = string.IsNullOrWhiteSpace(r.Unit) ? "unit" : r.Unit.Trim();
        item.ReorderLevel = r.ReorderLevel;
        item.SalePrice = r.SalePrice;
        item.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    /// <summary>Starter items: one per drug of the master (so prescriptions can be dispensed), plus common consumables.</summary>
    public static async Task EnsureDefaultsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        var tenant = db.CurrentTenantId;
        var existing = await db.InventoryItems.IgnoreQueryFilters().Where(i => i.TenantId == tenant).Select(i => new { i.Code, i.DrugId }).ToListAsync(ct);
        if (existing.Count > 0) return; // seeded once; admins own the list afterwards
        var codes = new HashSet<string>();
        foreach (var d in await db.Drugs.IgnoreQueryFilters().Where(x => x.TenantId == tenant && !x.IsDeleted).ToListAsync(ct))
        {
            var code = new string($"{d.Name}-{d.Strength}".ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
            code = code[..Math.Min(40, code.Length)];
            if (!codes.Add(code)) continue;
            var unit = d.Form?.ToLowerInvariant() switch { "syrup" or "suspension" => "bottle", "cream" or "ointment" or "gel" => "tube", "injection" => "vial", "inhaler" => "inhaler", var f => f ?? "unit" };
            db.InventoryItems.Add(new InventoryItem
            {
                Id = Guid.CreateVersion7(), Code = code, Name = $"{d.Name} {d.Strength}".Trim(), Category = ItemCategory.Medicine, DrugId = d.Id, Unit = unit,
                ReorderLevel = 20, SalePrice = 10,
            });
        }
        foreach (var (code, name, unit, price) in new[]
                 {
                     ("gauze_pack", "Gauze pack", "pack", 50m), ("crepe_bandage", "Crepe bandage", "roll", 150m), ("syringe_5ml", "Syringe 5 ml", "piece", 25m),
                     ("iv_cannula", "IV cannula", "piece", 120m), ("drip_set", "Drip set", "piece", 100m), ("sterile_gloves", "Sterile gloves", "pair", 60m),
                 })
            if (codes.Add(code))
                db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), Code = code, Name = name, Category = ItemCategory.Consumable, Unit = unit, ReorderLevel = 20, SalePrice = price });
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- stock

    public async Task<(List<BatchStockRow> Batches, List<StockAlert> Alerts)> StockAsync(Guid branchId, DateTime today, CancellationToken ct)
    {
        var items = await db.InventoryItems.ToDictionaryAsync(i => i.Id, ct);
        var batches = await db.StockBatches.Where(b => b.BranchId == branchId).ToListAsync(ct);
        var sums = (await db.StockMovements.Where(m => m.BranchId == branchId).Select(m => new { m.BatchId, m.Quantity }).ToListAsync(ct))
            .GroupBy(m => m.BatchId).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity));
        var rows = batches.Select(b => new BatchStockRow(b.ItemId, items.GetValueOrDefault(b.ItemId)?.Name ?? "", new BatchStock(b.Id, b.BatchNo, b.Expiry, sums.GetValueOrDefault(b.Id)))).ToList();
        var alerts = rows.GroupBy(r => r.ItemId).Where(g => items.ContainsKey(g.Key))
            .SelectMany(g => Stock.Alerts(g.Key, items[g.Key].Name, items[g.Key].ReorderLevel, g.Select(r => r.Stock).ToList(), today)).ToList();
        return (rows, alerts);
    }

    // ---------------------------------------------------------------- reports (FR-REP-01/02)

    public async Task<ReportSummary> SummaryAsync(Guid branchId, DateTime from, DateTime to, CancellationToken ct)
    {
        // Load a little more than the period in UTC; the shared calculator applies the clinic-local days.
        var start = from.Date.AddDays(-1);
        var end = to.Date.AddDays(2);
        var patients = await db.Patients.Where(p => p.BranchId == branchId && p.CreatedAt >= start && p.CreatedAt < end)
            .Select(p => new ReportPatient(p.Id, p.BranchId, p.CreatedAt)).ToListAsync(ct);
        var visits = await db.Encounters.Where(e => e.BranchId == branchId && e.StartedAt >= start && e.StartedAt < end)
            .Select(e => new ReportVisit(e.Id, e.BranchId, e.DoctorId, e.StartedAt)).ToListAsync(ct);
        var diagnoses = await (from d in db.Diagnoses
                               join e in db.Encounters on d.EncounterId equals e.Id
                               where e.BranchId == branchId && e.StartedAt >= start && e.StartedAt < end
                               select new ReportDiagnosis(d.EncounterId, e.BranchId, d.Code, d.Description, e.StartedAt)).ToListAsync(ct);
        var payments = await db.Payments.Where(p => p.BranchId == branchId && p.ReceivedAt >= start && p.ReceivedAt < end)
            .Select(p => new PaymentEntry(p.Id, p.ReceivedBy, p.BranchId, p.ReceivedAt, p.Kind, p.Method, p.Amount)).ToListAsync(ct);
        var dues = await DuesAsync(branchId, ct);
        var (_, alerts) = await StockAsync(branchId, DateTime.Today, ct);
        var doctors = await db.DoctorProfiles.ToDictionaryAsync(d => d.Id, d => d.FullName, ct);
        return ReportCalculator.Summarise(from, to, branchId, patients, visits, diagnoses, payments, dues, alerts, doctors);
    }

    private async Task<List<ReportDue>> DuesAsync(Guid branchId, CancellationToken ct)
    {
        var invoices = await db.Invoices.Where(i => i.BranchId == branchId && i.Status == InvoiceStatus.Final).ToListAsync(ct);
        var ids = invoices.Select(i => i.Id).ToList();
        var pays = (await db.Payments.Where(p => p.InvoiceId != null && ids.Contains(p.InvoiceId.Value)).Select(p => new { p.InvoiceId, p.Kind, p.Amount }).ToListAsync(ct))
            .ToLookup(p => p.InvoiceId!.Value);
        var credits = (await db.CreditNotes.Where(c => ids.Contains(c.InvoiceId) && c.Status == ApprovalStatus.Approved).Select(c => new { c.InvoiceId, c.Amount }).ToListAsync(ct))
            .ToLookup(c => c.InvoiceId);
        return invoices.Select(i =>
        {
            var share = i.PayerId is null ? i.Total : i.PatientAmount;
            var settled = pays[i.Id].Sum(p => PaymentKind.Settles(p.Kind) ? p.Amount : p.Kind == PaymentKind.Refund ? -p.Amount : 0);
            return new ReportDue(i.Id, i.InvoiceNo, i.PatientId, i.BranchId, i.IssuedAt, Math.Max(0, share - settled - credits[i.Id].Sum(c => c.Amount)));
        }).ToList();
    }

    /// <summary>FR-REP-03: records of an entity type at a branch, in the period, counted by a reportable field.</summary>
    public async Task<FieldBreakdown> BreakdownAsync(string entityType, string fieldKey, Guid branchId, DateTime from, DateTime to, CancellationToken ct)
    {
        var versionIds = await db.FormVersions.Where(v => v.EntityType == entityType).Select(v => v.Id).ToListAsync(ct);
        FieldDefinition? field = null;
        foreach (var vid in versionIds)
            if ((await FormDefinitionCache.GetAsync(db, vid, ct))?.Definition.FindField(fieldKey) is { } f) { field = f; if (f.Reportable) break; }
        if (field is null) throw new RequestValidationException("fieldKey", $"{entityType} has no field '{fieldKey}'.");
        if (!field.Reportable) throw new RequestValidationException("fieldKey", $"'{field.Label.En}' is not marked reportable.");

        var start = from.Date.AddDays(-1);
        var end = to.Date.AddDays(2);
        var rows = await db.FieldValues.Where(v => v.EntityType == entityType && v.FieldKey == fieldKey && v.BranchId == branchId)
            .Select(v => new { v.RecordId, v.ValueKind, v.ValueText, v.ValueNumber, v.ValueDate, v.ValueBool, v.ValueJson, v.CreatedAt }).ToListAsync(ct);
        // Records in scope: those whose value for the field was recorded in the period (the same rule the device uses).
        var records = rows.Where(r => r.CreatedAt >= start && r.CreatedAt < end && ReportCalculator.InPeriod(r.CreatedAt, from, to, TimeZoneInfo.Local))
            .Select(r => r.RecordId).Distinct().ToList();
        var values = rows.Select(r => (r.RecordId, (IReadOnlyList<string>)FormValues.AsStringList(FieldValueCodec.Decode(r.ValueKind, r.ValueText, r.ValueNumber, r.ValueDate, r.ValueBool, r.ValueJson))));
        var labels = field.Options?.ToDictionary(o => o.Code, o => o.Label.En) ?? [];
        return ReportCalculator.Breakdown(entityType, fieldKey, field.Label.En, records, values, code => labels.GetValueOrDefault(code, code));
    }

    private static InventoryItemDto ToDto(InventoryItem i) => new(i.Id, i.Code, i.Name, i.Category, i.DrugId, i.Unit, i.ReorderLevel, i.SalePrice, i.IsActive);
}

public sealed record BatchStockRow(Guid ItemId, string ItemName, BatchStock Stock);
