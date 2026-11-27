namespace Clinic.Shared.Clinical;

// ============================================================================ packages (FR-BIL-10)

/// <summary>One entitlement of a package: N uses of a service kind (optionally one code, e.g. procedure 'physio_session').</summary>
public sealed record PackageItem(string Kind, string? Code, int Count);

public static class PackageStatus
{
    public const string Active = "active";
    public const string Used = "used";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Active, Used, Expired, Cancelled };
}

/// <summary>
/// Which package entitlement (if any) covers a charge, and how many uses are left. Shared so the device offers the
/// cover offline and the server refuses a use beyond the count.
/// </summary>
public static class Packages
{
    public static bool Matches(PackageItem item, string kind, string? code) =>
        string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
        (item.Code is null || string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));

    public static int Remaining(IEnumerable<PackageItem> items, IEnumerable<(string Kind, string? Code)> used, string kind, string? code)
    {
        var list = items.ToList();
        var item = list.FirstOrDefault(i => Matches(i, kind, code));
        if (item is null) return 0;
        return Math.Max(0, item.Count - used.Count(u => Matches(item, u.Kind, u.Code)));
    }
}

// ============================================================================ panels / insurance (FR-BIL-12)

public static class PayerKind
{
    public const string Corporate = "corporate";
    public const string Insurance = "insurance";
    public const string Government = "government";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Corporate, Insurance, Government };
}

/// <summary>A payer's own price for a service kind/code (per-payer price list).</summary>
public sealed record PayerPrice(string Kind, string? Code, decimal Price);

public static class PanelBilling
{
    /// <summary>Patient pays co-pay % of the total; the payer pays the rest (rounded to the rupee in the patient's favour).</summary>
    public static (decimal Payer, decimal Patient) Split(decimal total, decimal coPayPercent)
    {
        var patient = Math.Ceiling(total * Math.Clamp(coPayPercent, 0, 100) / 100m);
        if (patient > total) patient = total;
        return (total - patient, patient);
    }

    public static decimal? PriceFor(IEnumerable<PayerPrice> prices, string kind, string? code) =>
        prices.Where(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                          (p.Code is null || string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
              .OrderByDescending(p => p.Code is not null).Select(p => (decimal?)p.Price).FirstOrDefault();
}

public sealed record AgeingBuckets(decimal Current, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public decimal Total => Current + Days31To60 + Days61To90 + Over90;
}

/// <summary>Receivables ageing: settlements pay the oldest invoices first (FIFO).</summary>
public static class Ageing
{
    public static AgeingBuckets Calculate(IEnumerable<(DateTime IssuedAt, decimal Amount)> invoices, decimal settled, DateTime asOf)
    {
        decimal current = 0, d60 = 0, d90 = 0, over = 0;
        var left = settled;
        foreach (var (issued, amount) in invoices.OrderBy(i => i.IssuedAt))
        {
            var open = amount;
            var use = Math.Min(open, left);
            open -= use;
            left -= use;
            if (open <= 0) continue;
            var days = (asOf.Date - issued.Date).Days;
            if (days <= 30) current += open;
            else if (days <= 60) d60 += open;
            else if (days <= 90) d90 += open;
            else over += open;
        }
        return new AgeingBuckets(current, d60, d90, over);
    }
}

// ============================================================================ credit notes (FR-BIL-14)

public static class CreditKind
{
    public const string CreditNote = "credit_note";
    public const string WriteOff = "write_off";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { CreditNote, WriteOff };
}

public static class ApprovalStatus
{
    public const string Requested = "requested";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Requested, Approved, Rejected };
}

// ============================================================================ doctor share (FR-DOC-04, FR-BIL-18)

/// <summary>A doctor's share rule for a service kind: a percentage of the collected line amount, or a fixed amount per line.</summary>
public sealed record ShareRule(string Kind, decimal? Percent, decimal? Fixed);

public sealed record ShareLine(Guid InvoiceId, string Description, string Kind, decimal LineAmount, decimal Collected, decimal Share);

/// <summary>
/// FR-BIL-18: share is earned only on money actually collected, and reversed by refunds. Collections on an invoice are
/// spread over its lines in proportion to their amounts; a fixed-amount rule earns the same proportion of the fixed sum.
/// </summary>
public static class DoctorShare
{
    public static IReadOnlyList<ShareLine> Calculate(
        IEnumerable<(Guid InvoiceId, decimal InvoiceTotal, decimal NetCollected, IReadOnlyList<(string Description, string Kind, decimal Amount, Guid? DoctorId)> Lines)> invoices,
        Guid doctorId, IReadOnlyList<ShareRule> rules)
    {
        var result = new List<ShareLine>();
        foreach (var inv in invoices)
        {
            if (inv.InvoiceTotal <= 0 || inv.NetCollected <= 0) continue;
            var ratio = Math.Min(1m, inv.NetCollected / inv.InvoiceTotal);
            foreach (var line in inv.Lines.Where(l => l.DoctorId == doctorId))
            {
                var rule = rules.FirstOrDefault(r => string.Equals(r.Kind, line.Kind, StringComparison.OrdinalIgnoreCase));
                if (rule is null) continue;
                var collected = LineAmounts.Round(line.Amount * ratio);
                var share = rule.Percent is { } pct
                    ? LineAmounts.Round(collected * pct / 100m)
                    : LineAmounts.Round((rule.Fixed ?? 0) * ratio);
                result.Add(new ShareLine(inv.InvoiceId, line.Description, line.Kind, line.Amount, collected, share));
            }
        }
        return result;
    }
}

public static class PayoutStatus
{
    public const string Draft = "draft";
    public const string Approved = "approved";
    public const string Paid = "paid";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Draft, Approved, Paid };
}

// ============================================================================ communication (§4.14)

public static class MessageChannel
{
    public const string Sms = "sms";
    public const string WhatsApp = "whatsapp";
    public const string Email = "email";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Sms, WhatsApp, Email };
}

public static class MessageStatus
{
    /// <summary>Written on a device; waits for sync, then for the server's sender.</summary>
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Delivered = "delivered";
    public const string Failed = "failed";

    /// <summary>Not sent: the patient opted out or has no address for the channel.</summary>
    public const string Skipped = "skipped";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Queued, Sent, Delivered, Failed, Skipped };
}

public static class MessageTemplateKeys
{
    public const string AppointmentReminder = "appointment_reminder";
    public const string FollowUp = "follow_up";
    public const string LabReady = "lab_ready";
    public const string PaymentDue = "payment_due";
    public const string Birthday = "birthday";
    public const string Feedback = "feedback";
}

/// <summary>FR-COM-01: <c>{name}</c> placeholders; unknown ones are left as they are so a typo is visible.</summary>
public static class TemplateText
{
    public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
        System.Text.RegularExpressions.Regex.Replace(template, @"\{(\w+)\}", m =>
            values.TryGetValue(m.Groups[1].Value, out var v) && v is not null ? v : m.Value);
}

// ============================================================================ feedback (§4.15)

public static class ComplaintStatus
{
    public const string Open = "open";
    public const string InProgress = "in_progress";
    public const string Resolved = "resolved";
    public const string Closed = "closed";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Open, InProgress, Resolved, Closed };

    public static string Display(string s) => s == InProgress ? "In progress" : s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
