namespace Clinic.Shared.Clinical;

// ============================================================================ labs (§4.5)

/// <summary>FR-LAB-02: Ordered → Collected → Resulted → Verified (or Cancelled). Verified results are locked.</summary>
public static class LabStatus
{
    public const string Ordered = "ordered";
    public const string Collected = "collected";
    public const string Resulted = "resulted";
    public const string Verified = "verified";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> Flow = [Ordered, Collected, Resulted, Verified];
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Ordered, Collected, Resulted, Verified, Cancelled };

    public static bool IsLocked(string status) => status is Verified or Cancelled;

    /// <summary>Forward moves only, one step at a time (re-entering results before verification is allowed).</summary>
    public static bool CanMove(string from, string to) =>
        (from, to) switch
        {
            (Ordered, Collected or Cancelled) => true,
            (Collected, Resulted or Cancelled) => true,
            (Resulted, Resulted or Verified) => true,
            _ => from == to,
        };

    public static string Display(string status) => status.Length == 0 ? status : char.ToUpperInvariant(status[0]) + status[1..];
}

public static class LabPriority
{
    public const string Routine = "routine";
    public const string Urgent = "urgent";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Routine, Urgent };
}

public static class SampleTypes
{
    public static readonly IReadOnlyList<string> All = ["blood", "serum", "plasma", "urine", "stool", "swab", "sputum", "other"];
}

// ============================================================================ billing (§4.6)

public static class InvoiceStatus
{
    /// <summary>Lines can still change.</summary>
    public const string Draft = "draft";

    /// <summary>Issued to the patient: lines and amounts are locked; payments and refunds continue.</summary>
    public const string Final = "final";

    /// <summary>Cancelled with a reason; it no longer counts in balances (payments on it must be refunded first).</summary>
    public const string Void = "void";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Draft, Final, Void };
}

/// <summary>Where an invoice line came from (FR-BIL-02). A source can be on one live invoice only.</summary>
public static class ChargeSource
{
    public const string Appointment = "appointment";
    public const string Procedure = "procedure";
    public const string LabResult = "lab";
    public const string Admission = "admission";
    public const string Service = "service";
    public const string Manual = "manual";

    /// <summary>FR-BIL-10: the sale of a package to a patient (SourceId = the patient's package).</summary>
    public const string Package = "package";

    /// <summary>Medicine dispensed by the pharmacy (SourceId = the dispensation).</summary>
    public const string Dispense = "dispense";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Appointment, Procedure, LabResult, Admission, Service, Manual, Package, Dispense };

    /// <summary>Sources that point at a record (and so cannot be billed twice).</summary>
    public static bool IsTracked(string source) => source is Appointment or Procedure or LabResult or Admission or Package or Dispense;
}

public static class ServiceKind
{
    public const string Consultation = "consultation";
    public const string Procedure = "procedure";
    public const string Lab = "lab";
    public const string Medicine = "medicine";
    public const string Room = "room";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Consultation, Procedure, Lab, Medicine, Room, Other };
}

public static class PaymentKind
{
    public const string Payment = "payment";
    public const string Refund = "refund";

    /// <summary>FR-BIL-13: money received without an invoice (advance / wallet); it lowers the patient balance.</summary>
    public const string Deposit = "deposit";

    /// <summary>FR-BIL-13: deposit credit applied to an invoice. Moves no cash, so it is left out of the day closing.</summary>
    public const string Allocation = "allocation";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Payment, Refund, Deposit, Allocation };

    /// <summary>Kinds that settle an invoice.</summary>
    public static bool Settles(string kind) => kind is Payment or Allocation;
}

/// <summary>FR-BIL-04 defaults (the 'payment_methods' lookup list; admins may add more).</summary>
public static class PaymentMethods
{
    public const string Cash = "cash";
    public const string Card = "card";
    public const string BankTransfer = "bank_transfer";
    public const string Wallet = "wallet";
}

public sealed record LineAmounts(decimal Quantity, decimal UnitPrice, decimal Discount, decimal TaxPercent)
{
    public decimal Gross => Round(Quantity * UnitPrice);
    public decimal Net => Gross - Discount;
    public decimal Tax => Round(Net * TaxPercent / 100m);
    public decimal Total => Net + Tax;

    public static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

public sealed record InvoiceTotals(decimal Subtotal, decimal Discount, decimal Tax, decimal Total);

/// <summary>
/// Invoice arithmetic shared by device and server: the server recomputes totals from the lines it holds and refuses
/// an invoice whose stored totals disagree (FR-SYN-13).
/// </summary>
public static class Billing
{
    public static InvoiceTotals Totals(IEnumerable<LineAmounts> lines, decimal invoiceDiscount)
    {
        var list = lines.ToList();
        var subtotal = list.Sum(l => l.Gross);
        var lineDiscounts = list.Sum(l => l.Discount);
        var tax = list.Sum(l => l.Tax);
        var discount = lineDiscounts + invoiceDiscount;
        return new InvoiceTotals(subtotal, discount, tax, subtotal - discount + tax);
    }

    /// <summary>Money still owed on an invoice: total − payments + refunds (never negative).</summary>
    public static decimal Due(decimal total, decimal paid, decimal refunded) => Math.Max(0, total - paid + refunded);

    public static string InvoiceNo(string branchCode, string deviceCode, long seq) => $"{branchCode}-{deviceCode}-{seq:D5}";
}

/// <summary>One payment row, as both sides see it, for cash closing.</summary>
public sealed record PaymentEntry(Guid Id, Guid ReceivedBy, Guid BranchId, DateTime ReceivedAt, string Kind, string Method, decimal Amount);

public sealed record MethodTotal(string Method, decimal Received, decimal Refunded)
{
    public decimal Net => Received - Refunded;
}

public sealed record DaySummary(DateTime BusinessDate, Guid BranchId, Guid? UserId, int Count, IReadOnlyList<MethodTotal> Methods)
{
    public decimal Received => Methods.Sum(m => m.Received);
    public decimal Refunded => Methods.Sum(m => m.Refunded);
    public decimal Net => Received - Refunded;
    public decimal Cash => Methods.FirstOrDefault(m => m.Method == PaymentMethods.Cash)?.Net ?? 0;
}

/// <summary>
/// FR-BIL-06 daily cash closing: the same calculation on the device (offline, from synced payments) and on the
/// server, so totals match across devices after sync (Phase 6 acceptance). Days are clinic-local.
/// </summary>
public static class CashClosingCalculator
{
    public static DaySummary Summarise(IEnumerable<PaymentEntry> payments, DateTime businessDate, Guid branchId, Guid? userId, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var day = businessDate.Date;
        var rows = payments
            .Where(p => p.Kind != PaymentKind.Allocation)
            .Where(p => p.BranchId == branchId && (userId is null || p.ReceivedBy == userId))
            .Where(p => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(p.ReceivedAt, DateTimeKind.Utc), zone).Date == day)
            .ToList();
        var methods = rows.GroupBy(p => p.Method).OrderBy(g => g.Key)
            .Select(g => new MethodTotal(g.Key,
                g.Where(p => p.Kind != PaymentKind.Refund).Sum(p => p.Amount),
                g.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount)))
            .ToList();
        return new DaySummary(day, branchId, userId, rows.Count, methods);
    }
}
