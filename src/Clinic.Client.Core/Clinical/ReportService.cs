using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Reports;

namespace Clinic.Client.Core.Clinical;

/// <summary>
/// FR-REP-01…03 on the device: the dashboard and reports from local data, offline ("as of last sync"), with the same
/// calculator the server uses, so the figures match once the device has synced. Export to Excel and PDF.
/// </summary>
public sealed class ReportService(LocalStore store, DynamicDataService forms, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ReportSummary> SummaryAsync(Guid branchId, DateTime from, DateTime to)
    {
        var patients = (await store.ListAsync<LocalPatient>(p => p.BranchId == branchId)).Select(p => new ReportPatient(p.Id, p.BranchId, p.CreatedAt));
        var encounters = await store.ListAsync<LocalEncounter>(e => e.BranchId == branchId);
        var visits = encounters.Select(e => new ReportVisit(e.Id, e.BranchId, e.DoctorId, e.StartedAt));
        var startedAt = encounters.ToDictionary(e => e.Id, e => e.StartedAt);
        var diagnoses = (await store.ListAsync<LocalDiagnosis>(d => d.BranchId == branchId))
            .Where(d => startedAt.ContainsKey(d.EncounterId))
            .Select(d => new ReportDiagnosis(d.EncounterId, d.BranchId, d.Code, d.Description, startedAt[d.EncounterId]));
        var payments = (await store.ListAsync<LocalPayment>(p => p.BranchId == branchId))
            .Select(p => new PaymentEntry(p.Id, p.ReceivedBy, p.BranchId, p.ReceivedAt, p.Kind, p.Method, p.Amount));

        var billing = new BillingService(store.Database, store, "", _clock);
        var dues = new List<ReportDue>();
        foreach (var inv in await store.ListAsync<LocalInvoice>(i => i.BranchId == branchId && i.Status == InvoiceStatus.Final))
            dues.Add(new ReportDue(inv.Id, inv.InvoiceNo, inv.PatientId, inv.BranchId, inv.IssuedAt, (await billing.SummaryAsync(inv.Id)).Due));

        var alerts = await new InventoryService(store, _clock).AlertsAsync(branchId);
        var doctors = (await store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        return ReportCalculator.Summarise(from, to, branchId, patients, visits, diagnoses, payments, dues, alerts, doctors);
    }

    /// <summary>Reportable fields (FR-DYN-07) of an entity type's published forms, for the group-by picker.</summary>
    public async Task<List<FieldDefinition>> ReportableFieldsAsync(string entityType, Guid branchId)
    {
        var version = await forms.ResolveFormAsync(entityType, branchId);
        return version?.Definition.AllFields.Where(f => f.Reportable && f.HoldsValue).ToList() ?? [];
    }

    public async Task<FieldBreakdown> BreakdownAsync(string entityType, string fieldKey, Guid branchId, DateTime from, DateTime to)
    {
        var field = (await ReportableFieldsAsync(entityType, branchId)).FirstOrDefault(f => f.Key == fieldKey)
                    ?? throw new CareException($"'{fieldKey}' is not a reportable field of {entityType}.");
        var rows = await store.ListAsync<LocalFieldValue>(v => v.EntityType == entityType && v.FieldKey == fieldKey && v.BranchId == branchId);
        var records = rows.Where(r => ReportCalculator.InPeriod(r.CreatedAt, from, to, TimeZoneInfo.Local)).Select(r => r.RecordId).Distinct().ToList();
        var values = rows.Select(r => (r.RecordId, (IReadOnlyList<string>)FormValues.AsStringList(
            FieldValueCodec.Decode(r.ValueKind, r.ValueText, r.ValueNumber, r.ValueDate, r.ValueBool, r.ValueJson))));

        var labels = field.Options?.ToDictionary(o => o.Code, o => o.Label.En) ?? [];
        if (field.LookupList is { } list)
            foreach (var i in await store.ListAsync<LocalLookupItem>(i => i.ListKey == list)) labels.TryAdd(i.Code, i.LabelEn);
        return ReportCalculator.Breakdown(entityType, fieldKey, field.Label.En, records, values, code => labels.GetValueOrDefault(code, code));
    }

    // ------------------------------------------------------------------ export (FR-REP-03)

    public static byte[] ToExcel(ReportSummary s, string clinic, FieldBreakdown? breakdown = null)
    {
        var period = $"{clinic} · {s.From:dd MMM yyyy} – {s.To:dd MMM yyyy}";
        var sheets = new List<Sheet>
        {
            new("Summary", ["Measure", "Value"],
            [
                ["New patients", s.NewPatients], ["Visits", s.Visits], ["Revenue (net)", s.RevenueNet],
                ["Pending invoices", s.PendingInvoices], ["Pending amount", s.PendingAmount],
            ], period),
            new("Visits by doctor", ["Doctor", "Visits"], s.VisitsByDoctor.Select(r => (IReadOnlyList<object?>)[r.Label, r.Count]).ToList(), period),
            new("Top diagnoses", ["Diagnosis", "Count"], s.TopDiagnoses.Select(r => (IReadOnlyList<object?>)[r.Label, r.Count]).ToList(), period),
            new("Revenue by method", ["Method", "Received", "Refunded", "Net"], s.RevenueByMethod.Select(r => (IReadOnlyList<object?>)[r.Key, r.Received, r.Refunded, r.Net]).ToList(), period),
            new("Revenue by day", ["Day", "Received", "Refunded", "Net"], s.RevenueByDay.Select(r => (IReadOnlyList<object?>)[r.Key, r.Received, r.Refunded, r.Net]).ToList(), period),
            new("Pending payments", ["Invoice", "Issued", "Due"], s.OldestPending.Select(r => (IReadOnlyList<object?>)[r.InvoiceNo, r.IssuedAt.ToLocalTime(), r.Due]).ToList(), period),
            new("Stock alerts", ["Alert", "Count"], s.StockAlerts.Select(r => (IReadOnlyList<object?>)[r.Label, r.Count]).ToList(), period),
        };
        if (breakdown is not null)
            sheets.Add(new Sheet(breakdown.FieldLabel, [breakdown.FieldLabel, "Records"],
                breakdown.Rows.Select(r => (IReadOnlyList<object?>)[r.Label, r.Count]).Append([ "(no value)", breakdown.WithoutValue ]).ToList(), period));
        return XlsxWriter.Write(sheets);
    }

    public static ClinicLetter ToLetter(ReportSummary s, string clinic, string branchLine, bool asOfLastSync, FieldBreakdown? breakdown = null)
    {
        var sections = new List<LetterSection>
        {
            new("Summary", [$"New patients: {s.NewPatients}", $"Visits: {s.Visits}", $"Revenue (net): Rs {s.RevenueNet:N0}",
                $"Pending: {s.PendingInvoices} invoice(s), Rs {s.PendingAmount:N0}"]),
            new("Visits by doctor", s.VisitsByDoctor.Select(r => $"{r.Label}: {r.Count}").DefaultIfEmpty("—").ToList()),
            new("Top diagnoses", s.TopDiagnoses.Select(r => $"{r.Label}: {r.Count}").DefaultIfEmpty("—").ToList()),
            new("Revenue by method", s.RevenueByMethod.Select(r => $"{r.Key}: Rs {r.Received:N0}" + (r.Refunded > 0 ? $" − Rs {r.Refunded:N0} refunds" : "") + $" = Rs {r.Net:N0}").DefaultIfEmpty("—").ToList()),
            new("Stock alerts", s.StockAlerts.Select(r => $"{r.Label}: {r.Count}").DefaultIfEmpty("None").ToList()),
        };
        if (breakdown is not null)
            sections.Add(new LetterSection($"By {breakdown.FieldLabel}", breakdown.Rows.Select(r => $"{r.Label}: {r.Count}").Append($"(no value): {breakdown.WithoutValue}").ToList()));
        return new ClinicLetter($"Report {s.From:dd MMM} – {s.To:dd MMM yyyy}", clinic, branchLine, "", "", asOfLastSync ? "As of last sync (offline data)" : "Server figures", "",
            DateTime.Now, sections);
    }
}
