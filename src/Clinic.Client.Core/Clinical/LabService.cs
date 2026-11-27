using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

public sealed class LabException(string message) : Exception(message);

/// <summary>One parameter on a report: value, unit, reference text and the abnormal flag from the form rules.</summary>
public sealed record LabValueLine(string Label, string Value, string? Unit, string? Reference, AbnormalSeverity Flag, string? FlagText);

/// <summary>
/// §4.5 on the device (FR-LAB-01…03): order tests from a consultation, collect samples, enter results in the test's
/// dynamic form (reference ranges and abnormal flags by gender/age come from its rules), verify, print the report.
/// Works offline; the lab and the doctor see each other's work after sync.
/// </summary>
public sealed class LabService(LocalStore store, DynamicDataService forms, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private Guid UserId => store.Context.UserId;

    public async Task<List<LocalLabTest>> TestsAsync(string? search = null)
    {
        var all = await store.ListAsync<LocalLabTest>(t => t.IsActive);
        var q = search?.Trim() ?? "";
        return all.Where(t => q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Department).ThenBy(t => t.Name).ToList();
    }

    /// <summary>The test's own LabResult form (context = test code), else the clinic's general one.</summary>
    public Task<FormVersionInfo?> ResolveFormAsync(LocalLabTest test, Guid branchId) =>
        forms.ResolveFormAsync(SyncEntityNames.LabResult, branchId, test.Code);

    public static EvaluationContext ContextFor(LocalLabResult r, IReadOnlyCollection<string>? roles = null, string language = "en") => new()
    {
        Roles = roles ?? [],
        Language = language,
        IsNewRecord = !LabStatus.IsLocked(r.Status),
        Variables = new Dictionary<string, object?>(PatientRules.FormVariables(r.PatientGender, r.PatientDateOfBirth, DateTime.Today))
        {
            ["test"] = r.TestCode,
        },
    };

    // ------------------------------------------------------------------ ordering (doctor)

    public async Task<LocalLabOrder> OrderAsync(Guid patientId, Guid branchId, IReadOnlyCollection<Guid> testIds, Guid? encounterId = null,
        Guid? doctorId = null, string priority = LabPriority.Routine, string? clinicalNotes = null)
    {
        if (testIds.Count == 0) throw new LabException("Choose at least one test.");
        if (!LabPriority.All.Contains(priority)) throw new LabException($"Unknown priority '{priority}'.");
        var patient = await store.GetAsync<LocalPatient>(patientId) ?? throw new LabException("Patient not found.");
        var encounter = encounterId is { } eid ? await store.GetAsync<LocalEncounter>(eid) : null;
        if (encounter is not null && encounter.PatientId != patientId) throw new LabException("That consultation belongs to another patient.");

        var tests = new List<(LocalLabTest Test, FormVersionInfo? Form)>();
        foreach (var id in testIds.Distinct())
        {
            var t = await store.GetAsync<LocalLabTest>(id) ?? throw new LabException("Test not found.");
            if (!t.IsActive) throw new LabException($"{t.Name} is no longer offered.");
            tests.Add((t, await ResolveFormAsync(t, branchId)));
        }

        var order = new LocalLabOrder
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, EncounterId = encounterId, CaseId = encounter?.CaseId,
            DoctorId = doctorId ?? encounter?.DoctorId, OrderedAt = UtcNow, OrderedBy = UserId, Priority = priority,
            ClinicalNotes = string.IsNullOrWhiteSpace(clinicalNotes) ? null : clinicalNotes.Trim(),
        };
        await store.WriteAsync(b =>
        {
            b.Insert(order);
            foreach (var (t, form) in tests)
                b.Insert(new LocalLabResult
                {
                    Id = Guid.NewGuid(), OrderId = order.Id, PatientId = patientId, BranchId = branchId, LabTestId = t.Id, TestCode = t.Code,
                    TestName = t.Name, SampleType = t.SampleType, Fee = t.Fee, Status = LabStatus.Ordered, FormVersionId = form?.Id,
                    PatientGender = patient.Gender, PatientDateOfBirth = patient.DateOfBirth,
                });
        });
        return order;
    }

    public async Task<List<LocalLabResult>> ResultsAsync(Guid orderId) =>
        (await store.ListAsync<LocalLabResult>(r => r.OrderId == orderId)).OrderBy(r => r.TestName).ToList();

    public async Task<List<LocalLabOrder>> ForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalLabOrder>(o => o.PatientId == patientId)).OrderByDescending(o => o.OrderedAt).ToList();

    public async Task<List<LocalLabOrder>> ForEncounterAsync(Guid encounterId) =>
        (await store.ListAsync<LocalLabOrder>(o => o.EncounterId == encounterId)).OrderBy(o => o.OrderedAt).ToList();

    /// <summary>The lab's work list: urgent first, then oldest, for the given statuses.</summary>
    public async Task<List<LocalLabResult>> WorkListAsync(Guid branchId, params string[] statuses)
    {
        statuses = statuses.Length == 0 ? [LabStatus.Ordered, LabStatus.Collected, LabStatus.Resulted] : statuses;
        var orders = (await store.ListAsync<LocalLabOrder>(o => o.BranchId == branchId)).ToDictionary(o => o.Id);
        return (await store.ListAsync<LocalLabResult>(r => r.BranchId == branchId))
            .Where(r => statuses.Contains(r.Status) && orders.ContainsKey(r.OrderId))
            .OrderBy(r => orders[r.OrderId].Priority == LabPriority.Urgent ? 0 : 1).ThenBy(r => orders[r.OrderId].OrderedAt).ToList();
    }

    // ------------------------------------------------------------------ lab work

    public async Task<LocalLabResult> CollectAsync(Guid resultId, string? sampleNo = null)
    {
        var r = await RequireAsync(resultId, LabStatus.Ordered, "collect a sample for");
        r.Status = LabStatus.Collected;
        r.CollectedAt = UtcNow;
        r.CollectedBy = UserId;
        r.SampleNo = string.IsNullOrWhiteSpace(sampleNo) ? $"{_clock.GetLocalNow():yyMMdd}-{r.Id.ToString("N")[..5].ToUpperInvariant()}" : sampleNo.Trim();
        await store.UpdateAsync(r);
        return r;
    }

    /// <summary>
    /// Saves the parameter values (validated by the test's form) and marks the test resulted. The abnormal flag is
    /// any parameter outside its range by the form's rules — for this patient's gender and age.
    /// </summary>
    public async Task<LocalLabResult> EnterResultsAsync(Guid resultId, JsonObject values, string? comment = null)
    {
        var r = await store.GetAsync<LocalLabResult>(resultId) ?? throw new LabException("Test not found.");
        if (LabStatus.IsLocked(r.Status)) throw new LabException(r.Status == LabStatus.Verified ? "This result is verified and locked." : "This test was cancelled.");
        if (r.Status == LabStatus.Ordered) throw new LabException("Collect the sample first.");
        var version = r.FormVersionId is { } v ? await forms.GetVersionAsync(v) : null;
        if (version is null) throw new LabException($"No result form is published for {r.TestName}. Ask the administrator to publish one.");

        var evaluation = version.Evaluator.Evaluate(values, ContextFor(r));
        if (evaluation.Errors.FirstOrDefault() is { Error: { } error }) throw new LabException(error);

        r.IsAbnormal = evaluation.Fields.Values.Any(f => f.Visible && f.Abnormal != AbnormalSeverity.None);
        r.Status = LabStatus.Resulted;
        r.ResultedAt = UtcNow;
        r.ResultedBy = UserId;
        r.Comment = string.IsNullOrWhiteSpace(comment) ? r.Comment : comment.Trim();
        await DynamicDataService.SaveRecordAsync(store, r, isNew: false, version, evaluation.Values, SyncEntityNames.LabResult, r.BranchId);
        return r;
    }

    /// <summary>FR-LAB-02: verification locks the result; the order completes when every test is verified or cancelled.</summary>
    public async Task<LocalLabResult> VerifyAsync(Guid resultId)
    {
        var r = await RequireAsync(resultId, LabStatus.Resulted, "verify");
        r.Status = LabStatus.Verified;
        r.VerifiedAt = UtcNow;
        r.VerifiedBy = UserId;
        await WriteWithOrderAsync(r);
        return r;
    }

    public async Task<LocalLabResult> CancelAsync(Guid resultId, string reason)
    {
        var r = await store.GetAsync<LocalLabResult>(resultId) ?? throw new LabException("Test not found.");
        if (LabStatus.IsLocked(r.Status) || r.Status == LabStatus.Resulted) throw new LabException("Only tests without results can be cancelled.");
        if (string.IsNullOrWhiteSpace(reason)) throw new LabException("Give a reason.");
        r.Status = LabStatus.Cancelled;
        r.CancelReason = reason.Trim();
        await WriteWithOrderAsync(r);
        return r;
    }

    private async Task WriteWithOrderAsync(LocalLabResult r)
    {
        var siblings = await ResultsAsync(r.OrderId);
        var order = await store.GetAsync<LocalLabOrder>(r.OrderId);
        var complete = siblings.All(s => s.Id == r.Id ? LabStatus.IsLocked(r.Status) : LabStatus.IsLocked(s.Status));
        await store.WriteAsync(b =>
        {
            b.Update(r);
            if (order is not null && complete && order.Status != "complete")
            {
                order.Status = "complete";
                b.Update(order);
            }
        });
    }

    private async Task<LocalLabResult> RequireAsync(Guid id, string status, string action)
    {
        var r = await store.GetAsync<LocalLabResult>(id) ?? throw new LabException("Test not found.");
        if (r.Status != status) throw new LabException($"Cannot {action} a test that is {LabStatus.Display(r.Status).ToLowerInvariant()}.");
        return r;
    }

    // ------------------------------------------------------------------ reading and reporting

    public async Task<IReadOnlyList<LabValueLine>> ValuesAsync(LocalLabResult r)
    {
        if (r.FormVersionId is not { } v || await forms.GetVersionAsync(v) is not { } version) return [];
        var values = await forms.LoadValuesAsync(SyncEntityNames.LabResult, r.Id);
        var evaluation = version.Evaluator.Evaluate(values, ContextFor(r));
        return version.Definition.AllFields
            .Where(f => f.HoldsValue && !FormValues.IsEmpty(evaluation.Values[f.Key]) && evaluation.State(f.Key).Visible)
            .Select(f =>
            {
                var st = evaluation.State(f.Key);
                var node = evaluation.Values[f.Key];
                var text = FormValues.AsDecimal(node) is { } d && node is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    ? d.ToString(f.Decimals is { } n ? "F" + n : "0.##")
                    : FormValues.AsString(node) ?? node!.ToJsonString();
                return new LabValueLine(f.Label.En, text, f.Unit, f.Help?.En, st.Abnormal, st.AbnormalMessage);
            }).ToList();
    }

    /// <summary>FR-LAB-02 report PDF content: results of the order (verified ones as final; others marked provisional).</summary>
    public async Task<ClinicLetter> BuildReportAsync(Guid orderId, string clinicName, string branchLine)
    {
        var order = await store.GetAsync<LocalLabOrder>(orderId) ?? throw new LabException("Order not found.");
        var patient = await store.GetAsync<LocalPatient>(order.PatientId) ?? throw new LabException("Patient not found.");
        var doctor = order.DoctorId is { } d ? await store.GetAsync<LocalDoctor>(d) : null;
        var sections = new List<LetterSection>();
        var allVerified = true;
        foreach (var r in (await ResultsAsync(orderId)).Where(r => r.Status is LabStatus.Resulted or LabStatus.Verified))
        {
            allVerified &= r.Status == LabStatus.Verified;
            var lines = (await ValuesAsync(r)).Select(v =>
                $"{v.Label}: {v.Value}{(v.Unit is null ? "" : " " + v.Unit)}" +
                (v.Flag == AbnormalSeverity.None ? "" : v.Flag == AbnormalSeverity.Critical ? $"   ▲▲ {v.FlagText}" : $"   ▲ {v.FlagText}") +
                (v.Reference is null ? "" : $"   ({v.Reference})")).ToList();
            if (r.Comment is { } c) lines.Add($"Comment: {c}");
            lines.Add(r.Status == LabStatus.Verified ? $"Verified {r.VerifiedAt?.ToLocalTime():dd MMM yyyy HH:mm}" : "Provisional — not yet verified");
            sections.Add(new LetterSection($"{r.TestName} · sample {r.SampleNo}", lines));
        }
        if (sections.Count == 0) throw new LabException("No results to report yet.");
        var age = patient.DateOfBirth is { } dob ? $" · {PatientRules.AgeYears(dob, DateTime.Today)} y" : "";
        return new ClinicLetter(allVerified ? "Laboratory report" : "Laboratory report (provisional)", clinicName, branchLine,
            doctor is null ? "" : $"Ref. {doctor.FullName}", "", patient.FullName, $"MRN {patient.Mrn} · {patient.Gender}{age}",
            order.OrderedAt.ToLocalTime(), sections, SignatureLabel: "Laboratory", SignatureCaption: allVerified ? "Verified electronically" : "Not verified");
    }
}
