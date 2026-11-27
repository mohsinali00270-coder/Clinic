using System.Text.Json;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

public sealed class CareException(string message) : Exception(message);

/// <summary>A point of a numeric trend (FR-CASE-04), e.g. weight or blood pressure across the case's visits.</summary>
public sealed record TrendPoint(DateTime At, decimal Value);

public sealed record Trend(string FieldKey, string Label, string? Unit, IReadOnlyList<TrendPoint> Points)
{
    public decimal? First => Points.Count > 0 ? Points[0].Value : null;
    public decimal? Last => Points.Count > 0 ? Points[^1].Value : null;
}

/// <summary>FR-CASE-04 progress view: sessions done vs planned, overdue steps and trends.</summary>
public sealed record CaseProgress(
    int SessionsDone,
    int? SessionsPlanned,
    int StepsDone,
    int StepsTotal,
    IReadOnlyList<LocalCarePlanStep> OverdueSteps,
    LocalCarePlanStep? NextStep,
    IReadOnlyList<Trend> Trends,
    decimal? ProceduresTotal)
{
    public double? Percent => SessionsPlanned is > 0 ? Math.Min(1.0, (double)SessionsDone / SessionsPlanned.Value) : null;
}

public sealed record CaseTimelineItem(DateTime At, string Kind, string Title, string? Detail, Guid RecordId);

/// <summary>
/// §4.10 on the device (FR-CASE-01…08): open a case from its type (form, care plan, team), link visits and
/// procedures, keep notes, follow progress and close it with an outcome. Works offline; rows sync per FR-SYN rules.
/// </summary>
public sealed class CaseService(LocalStore store, DynamicDataService forms, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private DateTime Today => _clock.GetLocalNow().Date;
    private Guid UserId => store.Context.UserId;

    public async Task<List<LocalCaseType>> TypesAsync(string? specialty = null) =>
        (await store.ListAsync<LocalCaseType>(t => t.IsActive))
        .OrderBy(t => specialty is not null && string.Equals(t.Specialty, specialty, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(t => t.Name).ToList();

    public static IReadOnlyList<CasePlanTemplateStep> PlanOf(LocalCaseType t) =>
        JsonSerializer.Deserialize<List<CasePlanTemplateStep>>(t.DefaultPlanJson, LocalJson.Options) ?? [];

    /// <summary>The Case form for a type: a form whose context is the type code wins over the general one.</summary>
    public Task<FormVersionInfo?> ResolveFormAsync(LocalCaseType type, Guid branchId) =>
        forms.ResolveFormAsync(SyncEntityNames.Case, branchId, type.Code);

    public static EvaluationContext ContextFor(LocalCase c, IReadOnlyCollection<string> roles, string language = "en") => new()
    {
        Roles = roles,
        Language = language,
        IsNewRecord = !CaseStatus.IsFinished(c.Status),
        Variables = new Dictionary<string, object?>(PatientRules.FormVariables(c.PatientGender, c.PatientDateOfBirth, DateTime.Today))
        {
            ["caseType"] = c.CaseTypeCode,
        },
    };

    // ------------------------------------------------------------------ open / read

    /// <summary>FR-CASE-01/02/05/06: opens a case; the type's default plan becomes dated steps and the doctor joins as primary.</summary>
    public async Task<LocalCase> OpenAsync(Guid patientId, Guid caseTypeId, Guid branchId, Guid? primaryDoctorId,
        string? title = null, JsonObject? values = null, DateTime? openedOn = null)
    {
        var patient = await store.GetAsync<LocalPatient>(patientId) ?? throw new CareException("Patient not found.");
        var type = await store.GetAsync<LocalCaseType>(caseTypeId) ?? throw new CareException("Case type not found.");
        if (!type.IsActive) throw new CareException($"'{type.Name}' is no longer in use.");
        var doctor = primaryDoctorId is { } d ? await store.GetAsync<LocalDoctor>(d) : null;
        var opened = (openedOn ?? Today).Date;

        var c = new LocalCase
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, CaseTypeId = type.Id, CaseTypeCode = type.Code,
            Title = string.IsNullOrWhiteSpace(title) ? $"{type.Name} {opened:yyyy}" : title.Trim(),
            PrimaryDoctorId = doctor?.Id, OpenedAt = DateTime.SpecifyKind(opened, DateTimeKind.Local).ToUniversalTime(), Status = CaseStatus.Open,
            PlannedSessions = type.ExpectedSessions, FollowUpIntervalDays = type.FollowUpIntervalDays,
            PatientGender = patient.Gender, PatientDateOfBirth = patient.DateOfBirth,
        };
        var version = await ResolveFormAsync(type, branchId);
        JsonObject? evaluated = null;
        if (version is not null)
        {
            var result = version.Evaluator.Evaluate(values ?? [], ContextFor(c, []));
            if (values is not null && result.Errors.FirstOrDefault() is { Error: { } error }) throw new CareException(error);
            evaluated = result.Values;
        }

        await store.WriteAsync(b =>
        {
            c.FormVersionId = version?.Id;
            b.Insert(c);
            if (version is not null)
            {
                c.DynamicData = DynamicDataService.StageValues(b, SyncEntityNames.Case, c.Id, branchId, version, evaluated ?? []);
                b.Update(c);
            }
            var order = 0;
            foreach (var step in PlanOf(type))
                b.Insert(new LocalCarePlanStep
                {
                    Id = Guid.NewGuid(), CaseId = c.Id, BranchId = branchId, SortOrder = order += 10, Kind = step.Kind, Title = step.Title,
                    DueDate = opened.AddDays(step.OffsetDays), Status = CarePlanStepStatus.Planned,
                });
            if (doctor is not null)
                b.Insert(new LocalCaseMember { Id = Guid.NewGuid(), CaseId = c.Id, BranchId = branchId, DoctorId = doctor.Id, UserId = doctor.UserId, Name = doctor.FullName, Role = CaseMemberRole.Primary });
        });
        return c;
    }

    public Task<LocalCase?> GetAsync(Guid id) => store.GetAsync<LocalCase>(id);

    public async Task<List<LocalCase>> ForPatientAsync(Guid patientId, bool openOnly = false) =>
        (await store.ListAsync<LocalCase>(c => c.PatientId == patientId))
        .Where(c => !openOnly || !CaseStatus.IsFinished(c.Status))
        .OrderBy(c => CaseStatus.IsFinished(c.Status)).ThenByDescending(c => c.OpenedAt).ToList();

    /// <summary>FR-DOC-06: open cases where the doctor is primary or a team member.</summary>
    public async Task<List<LocalCase>> OpenForDoctorAsync(Guid doctorId)
    {
        var memberOf = (await store.ListAsync<LocalCaseMember>(m => m.DoctorId == doctorId)).Select(m => m.CaseId).ToHashSet();
        return (await store.ListAsync<LocalCase>())
            .Where(c => !CaseStatus.IsFinished(c.Status) && (c.PrimaryDoctorId == doctorId || memberOf.Contains(c.Id)))
            .OrderByDescending(c => c.OpenedAt).ToList();
    }

    public Task<FormVersionInfo?> FormAsync(LocalCase c) =>
        c.FormVersionId is { } v ? forms.GetVersionAsync(v) : Task.FromResult<FormVersionInfo?>(null);

    public async Task SaveDetailsAsync(Guid caseId, string title, Guid? primaryDoctorId, int? plannedSessions, JsonObject? values)
    {
        var c = await RequireOpenAsync(caseId);
        if (string.IsNullOrWhiteSpace(title)) throw new CareException("A case needs a title.");
        c.Title = title.Trim();
        c.PrimaryDoctorId = primaryDoctorId;
        c.PlannedSessions = plannedSessions;
        var version = await FormAsync(c);
        if (version is null || values is null) { await store.UpdateAsync(c); return; }
        await DynamicDataService.SaveRecordAsync(store, c, isNew: false, version, values, SyncEntityNames.Case, c.BranchId);
    }

    // ------------------------------------------------------------------ status (FR-CASE-01/07)

    public Task<LocalCase> HoldAsync(Guid caseId) => SetStatusAsync(caseId, CaseStatus.OnHold);
    public Task<LocalCase> ResumeAsync(Guid caseId) => SetStatusAsync(caseId, CaseStatus.Open);

    private async Task<LocalCase> SetStatusAsync(Guid caseId, string status)
    {
        var c = await RequireOpenAsync(caseId);
        c.Status = status;
        await store.UpdateAsync(c);
        return c;
    }

    /// <summary>
    /// FR-CASE-07: closes with an outcome and a closing summary. Steps still planned are marked skipped first, so the
    /// plan does not keep raising overdue reminders.
    /// </summary>
    public async Task<LocalCase> CloseAsync(Guid caseId, string outcome, string? summary, bool referred = false)
    {
        var c = await RequireOpenAsync(caseId);
        if (string.IsNullOrWhiteSpace(outcome)) throw new CareException("Choose an outcome to close the case.");
        var open = await store.ListAsync<LocalCarePlanStep>(s => s.CaseId == caseId && s.Status == CarePlanStepStatus.Planned);
        await store.WriteAsync(b =>
        {
            foreach (var s in open)
            {
                s.Status = CarePlanStepStatus.Skipped;
                s.Notes = string.IsNullOrWhiteSpace(s.Notes) ? "Case closed" : s.Notes;
                b.Update(s);
            }
            c.Status = referred ? CaseStatus.Referred : CaseStatus.Closed;
            c.Outcome = outcome;
            c.ClosingSummary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
            c.ClosedAt = UtcNow;
            c.ClosedBy = UserId;
            b.Update(c);
        });
        return c;
    }

    public async Task<LocalCase> ReopenAsync(Guid caseId)
    {
        var c = await store.GetAsync<LocalCase>(caseId) ?? throw new CareException("Case not found.");
        if (!CaseStatus.IsFinished(c.Status)) return c;
        c.Status = CaseStatus.Open;
        c.ClosedAt = null;
        c.ClosedBy = null;
        c.Outcome = null;
        await store.UpdateAsync(c);
        return c;
    }

    /// <summary>A draft closing summary from the case's own records (FR-AI-15 proper — the AI version — is Phase 9).</summary>
    public async Task<string> DraftClosingSummaryAsync(Guid caseId)
    {
        var c = await store.GetAsync<LocalCase>(caseId) ?? throw new CareException("Case not found.");
        var progress = await ProgressAsync(caseId);
        var encounters = await EncountersAsync(caseId);
        var diagnoses = new List<string>();
        foreach (var e in encounters)
            diagnoses.AddRange((await store.ListAsync<LocalDiagnosis>(d => d.EncounterId == e.Id)).Select(d => AmendmentText.Diagnosis(d.Code, d.Description)));

        var lines = new List<string>
        {
            $"{c.Title}: opened {c.OpenedAt.ToLocalTime():dd MMM yyyy}, {progress.SessionsDone} session(s)" +
            (progress.SessionsPlanned is { } p ? $" of {p} planned" : "") + ".",
        };
        if (diagnoses.Count > 0) lines.Add("Diagnoses: " + string.Join("; ", diagnoses.Distinct()) + ".");
        foreach (var t in progress.Trends.Where(t => t.Points.Count >= 2))
            lines.Add($"{t.Label}: {t.First:0.##} → {t.Last:0.##}{(t.Unit is null ? "" : " " + t.Unit)}.");
        if (progress.StepsTotal > 0) lines.Add($"Care plan: {progress.StepsDone} of {progress.StepsTotal} steps done.");
        return string.Join(" ", lines);
    }

    // ------------------------------------------------------------------ care plan (FR-CASE-05)

    public async Task<List<LocalCarePlanStep>> StepsAsync(Guid caseId) =>
        (await store.ListAsync<LocalCarePlanStep>(s => s.CaseId == caseId)).OrderBy(s => s.SortOrder).ThenBy(s => s.DueDate).ToList();

    public async Task<LocalCarePlanStep> AddStepAsync(Guid caseId, string kind, string title, DateTime? dueDate)
    {
        var c = await RequireOpenAsync(caseId);
        if (!CarePlanStepKind.All.Contains(kind)) throw new CareException($"Unknown step kind '{kind}'.");
        if (string.IsNullOrWhiteSpace(title)) throw new CareException("Describe the step.");
        var last = (await StepsAsync(caseId)).LastOrDefault()?.SortOrder ?? 0;
        return await store.InsertAsync(new LocalCarePlanStep
        {
            Id = Guid.NewGuid(), CaseId = caseId, BranchId = c.BranchId, SortOrder = last + 10, Kind = kind, Title = title.Trim(),
            DueDate = dueDate?.Date, Status = CarePlanStepStatus.Planned,
        });
    }

    public async Task CompleteStepAsync(Guid stepId, Guid? linkedRecordId = null, string? notes = null)
    {
        var s = await RequireStepAsync(stepId);
        s.Status = CarePlanStepStatus.Done;
        s.CompletedAt = UtcNow;
        s.CompletedBy = UserId;
        s.LinkedRecordId = linkedRecordId ?? s.LinkedRecordId;
        if (!string.IsNullOrWhiteSpace(notes)) s.Notes = notes.Trim();
        await store.UpdateAsync(s);
    }

    public async Task SkipStepAsync(Guid stepId, string? reason)
    {
        var s = await RequireStepAsync(stepId);
        s.Status = CarePlanStepStatus.Skipped;
        s.Notes = string.IsNullOrWhiteSpace(reason) ? s.Notes : reason.Trim();
        await store.UpdateAsync(s);
    }

    public async Task RescheduleStepAsync(Guid stepId, DateTime dueDate)
    {
        var s = await RequireStepAsync(stepId);
        s.DueDate = dueDate.Date;
        if (s.Status == CarePlanStepStatus.Skipped) s.Status = CarePlanStepStatus.Planned;
        await store.UpdateAsync(s);
    }

    public async Task RemoveStepAsync(Guid stepId)
    {
        var s = await RequireStepAsync(stepId);
        await store.DeleteAsync<LocalCarePlanStep>(s.Id);
    }

    private async Task<LocalCarePlanStep> RequireStepAsync(Guid stepId)
    {
        var s = await store.GetAsync<LocalCarePlanStep>(stepId) ?? throw new CareException("Step not found.");
        await RequireOpenAsync(s.CaseId);
        return s;
    }

    // ------------------------------------------------------------------ team and notes (FR-CASE-06)

    public async Task<List<LocalCaseMember>> TeamAsync(Guid caseId) =>
        (await store.ListAsync<LocalCaseMember>(m => m.CaseId == caseId)).OrderBy(m => m.Role == CaseMemberRole.Primary ? 0 : 1).ThenBy(m => m.Name).ToList();

    public async Task<LocalCaseMember> AddDoctorAsync(Guid caseId, Guid doctorId, string role = CaseMemberRole.Consulting)
    {
        var c = await RequireOpenAsync(caseId);
        var doctor = await store.GetAsync<LocalDoctor>(doctorId) ?? throw new CareException("Doctor not found.");
        return await AddMemberAsync(c, doctor.FullName, role, doctor.Id, doctor.UserId);
    }

    public async Task<LocalCaseMember> AddMemberAsync(Guid caseId, string name, string role, Guid? userId = null)
    {
        var c = await RequireOpenAsync(caseId);
        return await AddMemberAsync(c, name, role, null, userId);
    }

    private async Task<LocalCaseMember> AddMemberAsync(LocalCase c, string name, string role, Guid? doctorId, Guid? userId)
    {
        if (!CaseMemberRole.All.Contains(role)) throw new CareException($"Unknown role '{role}'.");
        if (string.IsNullOrWhiteSpace(name)) throw new CareException("Enter the team member's name.");
        var team = await TeamAsync(c.Id);
        if (doctorId is not null && team.Any(m => m.DoctorId == doctorId)) throw new CareException($"{name} is already on the team.");
        var member = new LocalCaseMember { Id = Guid.NewGuid(), CaseId = c.Id, BranchId = c.BranchId, DoctorId = doctorId, UserId = userId, Name = name.Trim(), Role = role };
        await store.WriteAsync(b =>
        {
            if (role == CaseMemberRole.Primary)
            {
                // One primary: the previous one becomes consulting, and the case follows the new primary doctor.
                foreach (var m in team.Where(m => m.Role == CaseMemberRole.Primary)) { m.Role = CaseMemberRole.Consulting; b.Update(m); }
                if (doctorId is not null) { c.PrimaryDoctorId = doctorId; b.Update(c); }
            }
            b.Insert(member);
        });
        return member;
    }

    public async Task RemoveMemberAsync(Guid memberId)
    {
        var m = await store.GetAsync<LocalCaseMember>(memberId) ?? throw new CareException("Team member not found.");
        await RequireOpenAsync(m.CaseId);
        await store.DeleteAsync<LocalCaseMember>(memberId);
    }

    public async Task<List<LocalCaseNote>> NotesAsync(Guid caseId) =>
        (await store.ListAsync<LocalCaseNote>(n => n.CaseId == caseId)).OrderByDescending(n => n.WrittenAt).ToList();

    public async Task<LocalCaseNote> AddNoteAsync(Guid caseId, string body, string? authorName)
    {
        var c = await RequireOpenAsync(caseId);
        if (string.IsNullOrWhiteSpace(body)) throw new CareException("The note is empty.");
        return await store.InsertAsync(new LocalCaseNote
        {
            Id = Guid.NewGuid(), CaseId = caseId, BranchId = c.BranchId, Body = body.Trim(), WrittenAt = UtcNow, WrittenBy = UserId, AuthorName = authorName,
        });
    }

    // ------------------------------------------------------------------ links (FR-CASE-03)

    /// <summary>
    /// Files a visit under a case (one case per visit) or removes it (<paramref name="caseId"/> null). The first
    /// planned visit step due on or before the visit is marked done with it.
    /// </summary>
    public async Task LinkEncounterAsync(Guid encounterId, Guid? caseId)
    {
        var e = await store.GetAsync<LocalEncounter>(encounterId) ?? throw new CareException("Encounter not found.");
        if (e.CaseId == caseId) return;
        LocalCarePlanStep? step = null;
        if (caseId is { } id)
        {
            var c = await RequireOpenAsync(id);
            if (c.PatientId != e.PatientId) throw new CareException("That case belongs to another patient.");
            var visitDay = e.StartedAt.ToLocalTime().Date;
            step = (await StepsAsync(id)).FirstOrDefault(s =>
                s.Status == CarePlanStepStatus.Planned && s.Kind == CarePlanStepKind.Visit && (s.DueDate is null || s.DueDate.Value.Date <= visitDay.AddDays(3)));
        }
        await store.WriteAsync(b =>
        {
            e.CaseId = caseId;
            b.Update(e);
            if (step is not null)
            {
                step.Status = CarePlanStepStatus.Done;
                step.CompletedAt = UtcNow;
                step.CompletedBy = UserId;
                step.LinkedRecordId = e.Id;
                b.Update(step);
            }
        });
    }

    public async Task<List<LocalEncounter>> EncountersAsync(Guid caseId) =>
        (await store.ListAsync<LocalEncounter>(e => e.CaseId == caseId)).OrderBy(e => e.StartedAt).ToList();

    public async Task<List<LocalProcedure>> ProceduresAsync(Guid caseId) =>
        (await store.ListAsync<LocalProcedure>(p => p.CaseId == caseId)).OrderBy(p => p.PlannedAt ?? p.CreatedAt).ToList();

    // ------------------------------------------------------------------ progress and timeline (FR-CASE-04)

    public async Task<CaseProgress> ProgressAsync(Guid caseId)
    {
        var c = await store.GetAsync<LocalCase>(caseId) ?? throw new CareException("Case not found.");
        var steps = await StepsAsync(caseId);
        var encounters = await EncountersAsync(caseId);
        var procedures = (await ProceduresAsync(caseId)).Where(p => p.Status == ProcedureStatus.Done).ToList();

        var overdue = steps.Where(s => CarePlanStepStatus.IsOverdue(s.Status, s.DueDate, Today)).ToList();
        var next = steps.Where(s => s.Status == CarePlanStepStatus.Planned).OrderBy(s => s.DueDate ?? DateTime.MaxValue).FirstOrDefault();
        var fees = procedures.Where(p => p.Fee is not null).Sum(p => p.Fee!.Value);

        return new CaseProgress(
            SessionsDone: encounters.Count + procedures.Count(p => p.EncounterId is null || encounters.All(e => e.Id != p.EncounterId)),
            SessionsPlanned: c.PlannedSessions,
            StepsDone: steps.Count(s => s.Status == CarePlanStepStatus.Done),
            StepsTotal: steps.Count(s => s.Status != CarePlanStepStatus.Skipped),
            OverdueSteps: overdue,
            NextStep: next,
            Trends: await TrendsAsync(c, encounters),
            ProceduresTotal: procedures.Count > 0 ? fees : null);
    }

    /// <summary>Numeric vitals across the case: from vitals of linked visits, else from all vitals since the case opened.</summary>
    private async Task<IReadOnlyList<Trend>> TrendsAsync(LocalCase c, IReadOnlyList<LocalEncounter> encounters)
    {
        var ids = encounters.Select(e => e.Id).ToHashSet();
        var vitals = (await store.ListAsync<LocalVitals>(v => v.PatientId == c.PatientId))
            .Where(v => v.EncounterId is { } eid ? ids.Contains(eid) : v.RecordedAt >= c.OpenedAt)
            .Where(v => c.ClosedAt is null || v.RecordedAt <= c.ClosedAt)
            .OrderBy(v => v.RecordedAt).ToList();
        if (vitals.Count == 0) return [];

        var series = new Dictionary<string, (string Label, string? Unit, List<TrendPoint> Points)>();
        foreach (var v in vitals)
        {
            if (v.FormVersionId is not { } fv || await forms.GetVersionAsync(fv) is not { } version) continue;
            var values = await forms.LoadValuesAsync(SyncEntityNames.Vitals, v.Id);
            foreach (var f in version.Definition.AllFields.Where(f => f.IsNumeric || f.Type == FieldType.Calculated))
            {
                if (FormValues.AsDecimal(values[f.Key]) is not { } n) continue;
                if (!series.TryGetValue(f.Key, out var s)) series[f.Key] = s = (f.Label.En, f.Unit, []);
                s.Points.Add(new TrendPoint(v.RecordedAt, n));
            }
        }
        return series.Where(kv => kv.Value.Points.Count > 0)
            .Select(kv => new Trend(kv.Key, kv.Value.Label, kv.Value.Unit, kv.Value.Points)).ToList();
    }

    public async Task<List<CaseTimelineItem>> TimelineAsync(Guid caseId)
    {
        var c = await store.GetAsync<LocalCase>(caseId) ?? throw new CareException("Case not found.");
        var doctors = (await store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        var items = new List<CaseTimelineItem> { new(c.OpenedAt, "opened", $"Case opened · {c.Title}", null, c.Id) };

        foreach (var e in await EncountersAsync(caseId))
            items.Add(new(e.StartedAt, "encounter", $"Visit · {doctors.GetValueOrDefault(e.DoctorId, "Doctor")}", EncounterStatus.Display(e.Status), e.Id));
        foreach (var p in await ProceduresAsync(caseId))
            items.Add(new(p.CompletedAt ?? p.StartedAt ?? p.PlannedAt ?? p.CreatedAt, "procedure", $"{p.Name}", ProcedureStatus.Display(p.Status), p.Id));
        foreach (var a in await store.ListAsync<LocalAdmission>(a => a.CaseId == caseId))
            items.Add(new(a.AdmittedAt, "admission", "Day-care admission", a.Status, a.Id));
        foreach (var s in (await StepsAsync(caseId)).Where(s => s.Status == CarePlanStepStatus.Done && s.CompletedAt is not null))
            items.Add(new(s.CompletedAt!.Value, "step", $"Done: {s.Title}", s.Notes, s.Id));
        foreach (var n in await NotesAsync(caseId))
            items.Add(new(n.WrittenAt, "note", n.AuthorName is null ? "Note" : $"Note · {n.AuthorName}", n.Body, n.Id));
        foreach (var r in await store.ListAsync<LocalReferral>(r => r.CaseId == caseId))
            items.Add(new(r.ReferredAt, "referral", $"Referral ({r.Direction})", r.Reason, r.Id));
        foreach (var f in await store.ListAsync<LocalAttachment>(f => f.RecordId == caseId))
            items.Add(new(f.CreatedAt, "file", f.Caption ?? f.FileName, f.Category, f.Id));
        if (c.ClosedAt is { } closed) items.Add(new(closed, "closed", $"Case {CaseStatus.Display(c.Status).ToLowerInvariant()}", c.ClosingSummary, c.Id));

        return items.OrderByDescending(i => i.At).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<LocalCase> RequireOpenAsync(Guid caseId)
    {
        var c = await store.GetAsync<LocalCase>(caseId) ?? throw new CareException("Case not found.");
        if (CaseStatus.IsFinished(c.Status)) throw new CareException("This case is closed. Reopen it to make changes.");
        return c;
    }
}
