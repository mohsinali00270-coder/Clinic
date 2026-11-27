namespace Clinic.Shared.Clinical;

public static class ItemCategory
{
    public const string Medicine = "medicine";
    public const string Consumable = "consumable";
    public const string Reagent = "reagent";
    public const string Other = "other";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Medicine, Consumable, Reagent, Other };
}

/// <summary>FR-INV-02: every change of stock is a movement; quantity on hand is the sum, never an overwritten number.</summary>
public static class MovementKind
{
    public const string Purchase = "purchase";
    public const string Dispense = "dispense";
    public const string Consume = "consume";
    public const string Adjust = "adjust";
    public const string Expired = "expired";
    public const string Return = "return";
    public const string TransferOut = "transfer_out";
    public const string TransferIn = "transfer_in";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Purchase, Dispense, Consume, Adjust, Expired, Return, TransferOut, TransferIn };

    /// <summary>Kinds that add stock (positive quantity); the others take it away (negative). Adjust can be either.</summary>
    public static bool IsInbound(string kind) => kind is Purchase or Return or TransferIn;
    public static bool IsOutbound(string kind) => kind is Dispense or Consume or Expired or TransferOut;

    public static string? CheckSign(string kind, decimal quantity) =>
        quantity == 0 ? "The quantity cannot be zero."
        : IsInbound(kind) && quantity < 0 ? $"A {kind} adds stock; the quantity must be positive."
        : IsOutbound(kind) && quantity > 0 ? $"A {kind} takes stock away; the quantity must be negative."
        : null;
}

public sealed record BatchStock(Guid BatchId, string BatchNo, DateTime? Expiry, decimal OnHand);

public sealed record BatchPick(Guid BatchId, string BatchNo, DateTime? Expiry, decimal Quantity);

public sealed record StockAlert(Guid ItemId, string ItemName, string Kind, string Message, decimal OnHand, DateTime? Expiry = null, string? BatchNo = null);

public static class StockAlertKind
{
    public const string Low = "low";
    public const string NearExpiry = "near_expiry";
    public const string Expired = "expired";
    public const string Negative = "negative";
}

/// <summary>Stock arithmetic shared by device and server.</summary>
public static class Stock
{
    public const int NearExpiryDays = 60;

    /// <summary>
    /// FEFO: take from the batch that expires first, skipping expired batches. Returns the picks and the quantity
    /// that could not be covered (0 when there is enough).
    /// </summary>
    public static (IReadOnlyList<BatchPick> Picks, decimal Short) PickFefo(IEnumerable<BatchStock> batches, decimal quantity, DateTime today)
    {
        var picks = new List<BatchPick>();
        var left = quantity;
        foreach (var b in batches.Where(b => b.OnHand > 0 && (b.Expiry is null || b.Expiry.Value.Date >= today.Date))
                     .OrderBy(b => b.Expiry ?? DateTime.MaxValue).ThenBy(b => b.BatchNo))
        {
            if (left <= 0) break;
            var take = Math.Min(left, b.OnHand);
            picks.Add(new BatchPick(b.BatchId, b.BatchNo, b.Expiry, take));
            left -= take;
        }
        return (picks, Math.Max(0, left));
    }

    /// <summary>FR-INV-03: low stock (at or below the reorder level), near expiry (within 60 days), expired, negative (offline over-dispensing).</summary>
    public static IReadOnlyList<StockAlert> Alerts(Guid itemId, string itemName, decimal reorderLevel, IReadOnlyList<BatchStock> batches, DateTime today)
    {
        var alerts = new List<StockAlert>();
        var total = batches.Sum(b => b.OnHand);
        if (total < 0) alerts.Add(new StockAlert(itemId, itemName, StockAlertKind.Negative, $"Stock is {total:0.##}: more was dispensed than recorded. Count and adjust.", total));
        else if (total <= reorderLevel) alerts.Add(new StockAlert(itemId, itemName, StockAlertKind.Low, $"Only {total:0.##} left (reorder at {reorderLevel:0.##}).", total));
        foreach (var b in batches.Where(b => b.OnHand > 0 && b.Expiry is not null))
        {
            var days = (b.Expiry!.Value.Date - today.Date).Days;
            if (days < 0) alerts.Add(new StockAlert(itemId, itemName, StockAlertKind.Expired, $"Batch {b.BatchNo} expired on {b.Expiry:dd MMM yyyy} ({b.OnHand:0.##} left).", b.OnHand, b.Expiry, b.BatchNo));
            else if (days <= NearExpiryDays) alerts.Add(new StockAlert(itemId, itemName, StockAlertKind.NearExpiry, $"Batch {b.BatchNo} expires in {days} days ({b.OnHand:0.##} left).", b.OnHand, b.Expiry, b.BatchNo));
        }
        return alerts;
    }

    /// <summary>How many units a prescription line needs: dose count × times per day × days (at least 1).</summary>
    public static decimal QuantityFor(string? dose, string? frequency, int? days)
    {
        var perDose = 1m;
        if (dose is { } d)
        {
            var number = new string(d.TakeWhile(c => char.IsDigit(c) || c is '.' or '/').ToArray());
            if (number.Contains('/') && number.Split('/') is [var a, var b] && decimal.TryParse(a, out var na) && decimal.TryParse(b, out var nb) && nb != 0) perDose = na / nb;
            else if (decimal.TryParse(number, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0) perDose = n;
            // Syrups, drops, creams: dispensed as one pack, not counted per dose.
            if (d.Contains("ml", StringComparison.OrdinalIgnoreCase) || d.Contains("drop", StringComparison.OrdinalIgnoreCase) ||
                d.Contains("apply", StringComparison.OrdinalIgnoreCase) || d.Contains("puff", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        var perDay = (decimal)(Frequencies.Find(frequency)?.PerDay ?? 1);
        if (perDay == 0) perDay = 1;
        var total = Math.Ceiling(perDose * perDay * Math.Max(1, days ?? 1));
        return Math.Max(1, total);
    }
}

public static class InventoryRoutes
{
    public const string Items = "api/v1/inventory/items";
}

public sealed record InventoryItemDto(Guid Id, string Code, string Name, string Category, Guid? DrugId, string Unit, decimal ReorderLevel, decimal SalePrice, bool IsActive);

public sealed record SaveInventoryItemRequest(string Code, string Name, string Category, Guid? DrugId, string Unit, decimal ReorderLevel, decimal SalePrice, bool IsActive = true);
