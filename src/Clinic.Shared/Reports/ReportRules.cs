using Clinic.Shared.Clinical;

namespace Clinic.Shared.Reports;

public static class ReportRoutes
{
    public const string Summary = "api/v1/reports/summary";
    public const string Breakdown = "api/v1/reports/breakdown";
    public const string Stock = "api/v1/reports/stock";
}

// ------------------------------------------------------------------ inputs (plain rows; device and server fill them the same way)

public sealed record ReportPatient(Guid Id, Guid BranchId, DateTime CreatedAt);
public sealed record ReportVisit(Guid Id, Guid BranchId, Guid DoctorId, DateTime StartedAt);
public sealed record ReportDiagnosis(Guid EncounterId, Guid BranchId, string? Code, string Description, DateTime At);
public sealed record ReportDue(Guid InvoiceId, string InvoiceNo, Guid PatientId, Guid BranchId, DateTime IssuedAt, decimal Due);

// ------------------------------------------------------------------ outputs

public sealed record CountRow(string Key, string Label, int Count);
public sealed record MoneyRow(string Key, decimal Received, decimal Refunded)
{
    public decimal Net => Received - Refunded;
}

/// <summary>FR-REP-01 dashboard figures for a period and branch (FR-REP-02: the same on the device and the server).</summary>
public sealed record ReportSummary(
    DateTime From,
    DateTime To,
    Guid BranchId,
    int NewPatients,
    int Visits,
    IReadOnlyList<CountRow> VisitsByDoctor,
    IReadOnlyList<CountRow> TopDiagnoses,
    IReadOnlyList<MoneyRow> RevenueByMethod,
    IReadOnlyList<MoneyRow> RevenueByDay,
    int PendingInvoices,
    decimal PendingAmount,
    IReadOnlyList<ReportDue> OldestPending,
    IReadOnlyList<CountRow> StockAlerts)
{
    public decimal RevenueNet => RevenueByMethod.Sum(m => m.Net);
}

/// <summary>FR-REP-03: records counted by the value of a reportable dynamic field.</summary>
public sealed record FieldBreakdown(string EntityType, string FieldKey, string FieldLabel, IReadOnlyList<CountRow> Rows, int Records, int WithoutValue);

/// <summary>
/// The report arithmetic, shared so a device's offline report ("as of last sync") equals the server's once both
/// hold the same rows (Phase 7 acceptance). Periods are clinic-local calendar days, inclusive.
/// </summary>
public static class ReportCalculator
{
    public static ReportSummary Summarise(DateTime from, DateTime to, Guid branchId,
        IEnumerable<ReportPatient> patients, IEnumerable<ReportVisit> visits, IEnumerable<ReportDiagnosis> diagnoses,
        IEnumerable<PaymentEntry> payments, IEnumerable<ReportDue> dues, IEnumerable<StockAlert> alerts,
        IReadOnlyDictionary<Guid, string> doctorNames, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        bool In(DateTime utc) => InPeriod(utc, from, to, zone);

        var periodVisits = visits.Where(v => v.BranchId == branchId && In(v.StartedAt)).ToList();
        var byDoctor = periodVisits.GroupBy(v => v.DoctorId)
            .Select(g => new CountRow(g.Key.ToString(), doctorNames.GetValueOrDefault(g.Key, "Doctor"), g.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Label).ToList();

        var top = diagnoses.Where(d => d.BranchId == branchId && In(d.At))
            .GroupBy(d => d.Code ?? d.Description.Trim().ToLowerInvariant())
            .Select(g => new CountRow(g.Key, g.First().Code is { } c ? $"{c} {g.First().Description}" : g.First().Description, g.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Key).Take(10).ToList();

        var money = payments.Where(p => p.BranchId == branchId && p.Kind != PaymentKind.Allocation && In(p.ReceivedAt)).ToList();
        MoneyRow Money(string key, IEnumerable<PaymentEntry> rows)
        {
            var list = rows.ToList();
            return new MoneyRow(key, list.Where(p => p.Kind != PaymentKind.Refund).Sum(p => p.Amount), list.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount));
        }
        var byMethod = money.GroupBy(p => p.Method).OrderBy(g => g.Key).Select(g => Money(g.Key, g)).ToList();
        var byDay = money.GroupBy(p => Local(p.ReceivedAt, zone).Date).OrderBy(g => g.Key).Select(g => Money(g.Key.ToString("yyyy-MM-dd"), g)).ToList();

        var pending = dues.Where(d => d.BranchId == branchId && d.Due > 0).OrderBy(d => d.IssuedAt).ToList();
        var stock = alerts.GroupBy(a => a.Kind).OrderBy(g => g.Key).Select(g => new CountRow(g.Key, g.Key.Replace('_', ' '), g.Count())).ToList();

        return new ReportSummary(from.Date, to.Date, branchId,
            patients.Count(p => p.BranchId == branchId && In(p.CreatedAt)), periodVisits.Count, byDoctor, top, byMethod, byDay,
            pending.Count, pending.Sum(d => d.Due), pending.Take(10).ToList(), stock);
    }

    /// <summary>FR-REP-03: count records by a field's value; multi-select values count once per chosen option.</summary>
    public static FieldBreakdown Breakdown(string entityType, string fieldKey, string fieldLabel, IReadOnlyCollection<Guid> records,
        IEnumerable<(Guid RecordId, IReadOnlyList<string> Values)> values, Func<string, string>? labelOf = null)
    {
        var inScope = records.ToHashSet();
        var rows = values.Where(v => inScope.Contains(v.RecordId)).ToList();
        var withValue = rows.Where(r => r.Values.Count > 0).Select(r => r.RecordId).ToHashSet();
        var counts = rows.SelectMany(r => r.Values.Distinct().Select(v => v)).GroupBy(v => v)
            .Select(g => new CountRow(g.Key, labelOf?.Invoke(g.Key) ?? g.Key, g.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Label).ToList();
        return new FieldBreakdown(entityType, fieldKey, fieldLabel, counts, inScope.Count, inScope.Count - withValue.Count);
    }

    public static bool InPeriod(DateTime utc, DateTime from, DateTime to, TimeZoneInfo zone)
    {
        var day = Local(utc, zone).Date;
        return day >= from.Date && day <= to.Date;
    }

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}
