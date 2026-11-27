using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Clinic.App.Services;
using Clinic.Client.Core.Assist;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

/// <summary>A generic two-line row with a record id and an optional warning colour.</summary>
public sealed record CareRow(Guid Id, string Title, string? Detail, bool Warn = false, object? Tag = null)
{
    public Color TitleColor => Services.ThemeColors.Get(Warn ? "Danger" : "TextPrimary");
}

/// <summary>Helpers shared by the care screens: pickers, PDF output, the signed-in doctor.</summary>
internal static class CareUi
{
    public static async Task<string?> PickAsync(string title, IEnumerable<string> options)
    {
        var list = options.ToArray();
        if (list.Length == 0) return null;
        var pick = await Shell.Current.DisplayActionSheetAsync(title, "Cancel", null, list);
        return pick is null or "Cancel" ? null : pick;
    }

    public static async Task<LocalDoctor?> MyDoctorAsync(SyncCoordinator sync, SessionStore session) =>
        (await sync.Store.ListAsync<LocalDoctor>(d => d.IsActive)).FirstOrDefault(d => d.UserId == session.Me?.UserId);

    public static async Task OpenPdfAsync(string title, byte[] pdf)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, $"{title.Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        await Launcher.Default.OpenAsync(new OpenFileRequest(title, new ReadOnlyFile(path, "application/pdf")));
    }

    public static string BranchLine(SyncCoordinator sync, SessionStore session) =>
        session.Me?.Branches.FirstOrDefault(b => b.Id == sync.CurrentBranchId)?.Name ?? "";

    public static string Clinic(SessionStore session) => session.Me?.TenantName ?? "Clinic";
}

// =====================================================================================================
// §4.10 Case
// =====================================================================================================

/// <summary>FR-CASE-01…07: the case screen — details form, progress, care plan, team, notes, linked records, close.</summary>
public sealed partial class CaseViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid _caseId;
    private LocalCase? _case;

    public ObservableCollection<CareRow> Steps { get; } = [];
    public ObservableCollection<CareRow> Team { get; } = [];
    public ObservableCollection<CareRow> Notes { get; } = [];
    public ObservableCollection<CareRow> Timeline { get; } = [];
    public ObservableCollection<string> Trends { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsFinished { get; set; }
    [ObservableProperty] public partial bool IsOnHold { get; set; }
    [ObservableProperty] public partial DynamicFormModel? DetailsModel { get; set; }
    [ObservableProperty] public partial string? NewNote { get; set; }

    public bool CanManage => session.Has(Permissions.Cases.Manage);
    public bool CanEdit => CanManage && !IsFinished;
    partial void OnIsFinishedChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = RunAsync(async () =>
    {
        if (query.TryGetValue("caseId", out var c) && c is Guid id) _caseId = id;
        else if (query.TryGetValue("patientId", out var p) && p is Guid patientId) _caseId = (await OpenNewAsync(patientId))?.Id ?? Guid.Empty;
        if (_caseId == Guid.Empty) { await Ui.GoBackAsync(); return; }
        await ReloadAsync();
    });

    /// <summary>FR-CASE-01/02: pick a case type; the type brings its form, plan and expected sessions.</summary>
    private async Task<LocalCase?> OpenNewAsync(Guid patientId)
    {
        var me = await CareUi.MyDoctorAsync(sync, session);
        var types = await sync.Cases.TypesAsync(me?.Specialty);
        var pick = await CareUi.PickAsync("Open a case", types.Select(t => t.Name));
        var type = types.FirstOrDefault(t => t.Name == pick);
        if (type is null) return null;
        var title = await Ui.PromptAsync("Case title", "e.g. \"Knee physiotherapy\" or \"Pregnancy 2026\"", $"{type.Name} {DateTime.Today:yyyy}");
        if (title is null) return null;
        var doctor = me;
        if (doctor is null)
        {
            var doctors = await sync.Scheduling.DoctorsAtBranchAsync(sync.CurrentBranchId);
            var d = await CareUi.PickAsync("Primary doctor", doctors.Select(x => x.FullName));
            doctor = doctors.FirstOrDefault(x => x.FullName == d);
        }
        return await sync.Cases.OpenAsync(patientId, type.Id, sync.CurrentBranchId, doctor?.Id, title);
    }

    private async Task ReloadAsync()
    {
        _case = await sync.Cases.GetAsync(_caseId) ?? throw new InvalidOperationException("Case not found.");
        var c = _case;
        var patient = await sync.Store.GetAsync<LocalPatient>(c.PatientId);
        var doctors = (await sync.Store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        Title = c.Title;
        Subtitle = $"{patient?.FullName} · opened {c.OpenedAt.ToLocalTime():dd MMM yyyy}" +
                   (c.PrimaryDoctorId is { } pd ? $" · {doctors.GetValueOrDefault(pd)}" : "");
        IsFinished = CaseStatus.IsFinished(c.Status);
        IsOnHold = c.Status == CaseStatus.OnHold;
        StatusText = CaseStatus.Display(c.Status) + (c.Outcome is { } o ? $" · {o.Replace('_', ' ')}" : "");

        if (await sync.Cases.FormAsync(c) is { } form)
        {
            var values = await sync.Forms.LoadValuesAsync(SyncEntityNames.Case, c.Id);
            DetailsModel = new DynamicFormModel(form, values, CaseService.ContextFor(c, session.Me?.Roles ?? []), await sync.Forms.LoadLookupsAsync(form.Definition), isNewRecord: !IsFinished);
        }

        var progress = await sync.Cases.ProgressAsync(c.Id);
        ProgressValue = progress.Percent ?? 0;
        ProgressText = $"{progress.SessionsDone} session(s)" + (progress.SessionsPlanned is { } sp ? $" of {sp} planned" : "") +
                       $" · plan {progress.StepsDone}/{progress.StepsTotal}" +
                       (progress.OverdueSteps.Count > 0 ? $" · ⚠ {progress.OverdueSteps.Count} overdue" : "") +
                       (progress.NextStep is { } n ? $" · next: {n.Title} {n.DueDate:dd MMM}" : "") +
                       (progress.ProceduresTotal is { } t ? $" · procedures Rs {t:N0}" : "");
        Trends.Clear();
        foreach (var tr in progress.Trends.Where(t => t.Points.Count > 0).Take(6))
            Trends.Add($"{tr.Label}: {string.Join(" → ", tr.Points.TakeLast(6).Select(p => p.Value.ToString("0.#")))}{(tr.Unit is null ? "" : " " + tr.Unit)}");

        Steps.Clear();
        var today = DateTime.Today;
        foreach (var s in await sync.Cases.StepsAsync(c.Id))
        {
            var mark = s.Status switch { CarePlanStepStatus.Done => "✔", CarePlanStepStatus.Skipped => "–", _ => "○" };
            Steps.Add(new CareRow(s.Id, $"{mark} {s.Title}", $"{s.Kind} · due {s.DueDate:dd MMM yyyy}" + (s.Notes is null ? "" : $" · {s.Notes}"),
                CarePlanStepStatus.IsOverdue(s.Status, s.DueDate, today), s));
        }
        Team.Clear();
        foreach (var m in await sync.Cases.TeamAsync(c.Id)) Team.Add(new CareRow(m.Id, m.Name, m.Role));
        Notes.Clear();
        foreach (var note in await sync.Cases.NotesAsync(c.Id)) Notes.Add(new CareRow(note.Id, note.Body, $"{note.AuthorName} · {note.WrittenAt.ToLocalTime():dd MMM HH:mm}"));
        Timeline.Clear();
        foreach (var i in await sync.Cases.TimelineAsync(c.Id)) Timeline.Add(new CareRow(i.RecordId, i.Title, $"{i.At.ToLocalTime():dd MMM yyyy HH:mm}" + (i.Detail is null ? "" : $" · {i.Detail}"), Tag: i.Kind));
    }

    [RelayCommand]
    private Task SaveDetailsAsync() => RunAsync(async () =>
    {
        if (_case is null) return;
        if (DetailsModel is { } m && !m.Validate()) throw new InvalidOperationException("Check the highlighted fields.");
        await sync.Cases.SaveDetailsAsync(_case.Id, Title, _case.PrimaryDoctorId, _case.PlannedSessions, DetailsModel?.ValuesToSave());
        await ReloadAsync();
    });

    [RelayCommand]
    private Task HoldAsync() => RunAsync(async () =>
    {
        if (IsOnHold) await sync.Cases.ResumeAsync(_caseId); else await sync.Cases.HoldAsync(_caseId);
        await ReloadAsync();
    });

    /// <summary>FR-CASE-07: outcome from the 'case_outcomes' list and a closing summary (drafted from the case's records).</summary>
    [RelayCommand]
    private async Task CloseAsync()
    {
        var outcomes = (await sync.Store.ListAsync<LocalLookupItem>(i => i.ListKey == "case_outcomes" && i.IsActive)).OrderBy(i => i.SortOrder).ToList();
        var pick = await CareUi.PickAsync("Outcome", outcomes.Select(o => o.LabelEn));
        var outcome = outcomes.FirstOrDefault(o => o.LabelEn == pick);
        if (outcome is null) return;
        var draft = await sync.Cases.DraftClosingSummaryAsync(_caseId);
        var summary = await Shell.Current.DisplayPromptAsync("Closing summary", "Edit the draft if needed", "Close case", "Cancel", initialValue: draft, maxLength: 4000);
        if (summary is null) return;
        await RunAsync(async () =>
        {
            await sync.Cases.CloseAsync(_caseId, outcome.Code, summary, referred: outcome.Code == "referred");
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private Task ReopenAsync() => RunAsync(async () => { await sync.Cases.ReopenAsync(_caseId); await ReloadAsync(); });

    [RelayCommand]
    private async Task AddStepAsync()
    {
        var kind = await CareUi.PickAsync("Step", ["Visit", "Test", "Procedure", "Other"]);
        if (kind is null) return;
        var title = await Ui.PromptAsync("Care plan", "What is planned?");
        if (string.IsNullOrWhiteSpace(title)) return;
        var days = await Shell.Current.DisplayPromptAsync("Due", "In how many days?", "OK", "Cancel", initialValue: "7", keyboard: Keyboard.Numeric);
        if (days is null || !int.TryParse(days, out var d)) return;
        await RunAsync(async () =>
        {
            await sync.Cases.AddStepAsync(_caseId, kind.ToLowerInvariant(), title, DateTime.Today.AddDays(d));
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task StepActionAsync(CareRow row)
    {
        if (IsFinished || row.Tag is not LocalCarePlanStep s || s.Status == CarePlanStepStatus.Done) return;
        var action = await CareUi.PickAsync(s.Title, ["Mark done", "Reschedule", "Skip", "Remove"]);
        await RunAsync(async () =>
        {
            switch (action)
            {
                case "Mark done": await sync.Cases.CompleteStepAsync(s.Id); break;
                case "Skip": await sync.Cases.SkipStepAsync(s.Id, await Ui.PromptAsync("Skip", "Reason (optional)")); break;
                case "Remove": await sync.Cases.RemoveStepAsync(s.Id); break;
                case "Reschedule":
                    var days = await Shell.Current.DisplayPromptAsync("Reschedule", "Due in how many days from today?", "OK", "Cancel", initialValue: "7", keyboard: Keyboard.Numeric);
                    if (int.TryParse(days, out var d)) await sync.Cases.RescheduleStepAsync(s.Id, DateTime.Today.AddDays(d));
                    break;
                default: return;
            }
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task AddMemberAsync()
    {
        var doctors = await sync.Store.ListAsync<LocalDoctor>(d => d.IsActive);
        var options = doctors.Select(d => d.FullName).Append("Someone else (nurse, therapist)…").ToList();
        var pick = await CareUi.PickAsync("Add to the team", options);
        if (pick is null) return;
        var doctor = doctors.FirstOrDefault(d => d.FullName == pick);
        if (doctor is not null)
        {
            var role = await CareUi.PickAsync("Role", ["Consulting", "Primary"]);
            if (role is null) return;
            await RunAsync(async () => { await sync.Cases.AddDoctorAsync(_caseId, doctor.Id, role.ToLowerInvariant()); await ReloadAsync(); });
            return;
        }
        var name = await Ui.PromptAsync("Team member", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var r = await CareUi.PickAsync("Role", ["Nurse", "Therapist", "Consulting"]);
        if (r is null) return;
        await RunAsync(async () => { await sync.Cases.AddMemberAsync(_caseId, name, r.ToLowerInvariant()); await ReloadAsync(); });
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(CareRow row)
    {
        if (IsFinished || !await Ui.ConfirmAsync("Team", $"Remove {row.Title} from the team?", "Remove")) return;
        await RunAsync(async () => { await sync.Cases.RemoveMemberAsync(row.Id); await ReloadAsync(); });
    }

    [RelayCommand]
    private Task AddNoteAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewNote)) return;
        await sync.Cases.AddNoteAsync(_caseId, NewNote, session.Me?.FullName);
        NewNote = null;
        await ReloadAsync();
    });

    [RelayCommand]
    private Task NewProcedureAsync() => _case is null ? Task.CompletedTask
        : Ui.GoToAsync(Routes.Procedure, new Dictionary<string, object> { ["patientId"] = _case.PatientId, ["caseId"] = _case.Id });

    [RelayCommand]
    private Task ReferAsync() => _case is null ? Task.CompletedTask
        : Ui.GoToAsync(Routes.Referral, new Dictionary<string, object> { ["patientId"] = _case.PatientId, ["caseId"] = _case.Id });

    [RelayCommand]
    private Task OpenTimelineAsync(CareRow row) => row.Tag switch
    {
        "encounter" => Ui.GoToAsync(Routes.Encounter, new Dictionary<string, object> { ["encounterId"] = row.Id }),
        "procedure" => Ui.GoToAsync(Routes.Procedure, new Dictionary<string, object> { ["procedureId"] = row.Id }),
        "admission" => Ui.GoToAsync(Routes.Admission, new Dictionary<string, object> { ["admissionId"] = row.Id }),
        "referral" => Ui.GoToAsync(Routes.Referral, new Dictionary<string, object> { ["referralId"] = row.Id }),
        _ => Task.CompletedTask,
    };
}

// =====================================================================================================
// §4.11 Referral
// =====================================================================================================

/// <summary>FR-REF-01…03: create (internal / external / incoming), respond, print the letter.</summary>
public sealed partial class ReferralViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid _patientId;
    private Guid? _encounterId, _caseId, _referralId;
    private List<LocalDoctor> _doctors = [];

    public ObservableCollection<string> Directions { get; } = ["Internal (doctor / specialty)", "External (letter)", "Incoming (referred to us)"];
    public ObservableCollection<string> Priorities { get; } = ["Routine", "Urgent", "Emergency"];
    public ObservableCollection<string> Targets { get; } = [];

    [ObservableProperty] public partial bool IsNew { get; set; } = true;
    [ObservableProperty] public partial string PatientName { get; set; } = "";
    [ObservableProperty] public partial int DirectionIndex { get; set; }
    [ObservableProperty] public partial int PriorityIndex { get; set; }
    [ObservableProperty] public partial int TargetIndex { get; set; } = -1;
    [ObservableProperty] public partial string? Reason { get; set; }
    [ObservableProperty] public partial string? Facility { get; set; }
    [ObservableProperty] public partial string? ExternalDoctor { get; set; }
    [ObservableProperty] public partial string? Summary { get; set; }
    [ObservableProperty] public partial string? SourceName { get; set; }
    [ObservableProperty] public partial string Heading { get; set; } = "New referral";
    [ObservableProperty] public partial string? Details { get; set; }
    [ObservableProperty] public partial bool CanRespond { get; set; }
    [ObservableProperty] public partial bool CanPrint { get; set; }

    public bool IsInternal => DirectionIndex == 0;
    public bool IsExternal => DirectionIndex == 1;
    public bool IsIncoming => DirectionIndex == 2;

    partial void OnDirectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsInternal));
        OnPropertyChanged(nameof(IsExternal));
        OnPropertyChanged(nameof(IsIncoming));
        if (IsExternal && Summary is null && _encounterId is { } e)
            MainThread.BeginInvokeOnMainThread(async () => Summary ??= await sync.Referrals.SummaryFromEncounterAsync(e));
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = RunAsync(async () =>
    {
        _patientId = query.TryGetValue("patientId", out var p) && p is Guid pid ? pid : Guid.Empty;
        _encounterId = query.TryGetValue("encounterId", out var e) && e is Guid eid ? eid : null;
        _caseId = query.TryGetValue("caseId", out var c) && c is Guid cid ? cid : null;
        _referralId = query.TryGetValue("referralId", out var r) && r is Guid rid ? rid : null;
        _doctors = await sync.Store.ListAsync<LocalDoctor>(d => d.IsActive);
        await LoadAsync();
    });

    private async Task LoadAsync()
    {
        if (_referralId is { } id && await sync.Store.GetAsync<LocalReferral>(id) is { } r)
        {
            IsNew = false;
            _patientId = r.PatientId;
            var names = _doctors.ToDictionary(d => d.Id, d => d.FullName);
            Heading = $"{r.Direction} referral · {r.Status}";
            Details = $"{ReferralService.Describe(r, names)}\nPriority: {r.Priority} · {r.ReferredAt.ToLocalTime():dd MMM yyyy HH:mm}\n\n{r.Reason}" +
                      (r.Summary is null ? "" : $"\n\n{r.Summary}") + (r.ResponseNote is null ? "" : $"\n\nResponse: {r.ResponseNote}");
            var me = await CareUi.MyDoctorAsync(sync, session);
            CanRespond = r.Direction == ReferralDirection.Internal && ReferralStatus.IsOpen(r.Status) && session.Has(Permissions.Referrals.Manage) &&
                         (r.ToDoctorId == me?.Id || (r.ToDoctorId is null && me is not null && r.ToSpecialty == me.Specialty));
            CanPrint = r.Direction != ReferralDirection.Incoming;
        }
        PatientName = (await sync.Store.GetAsync<LocalPatient>(_patientId))?.FullName ?? "";
        Targets.Clear();
        foreach (var d in _doctors.OrderBy(d => d.FullName)) Targets.Add($"{d.FullName} ({d.Specialty})");
        foreach (var s in _doctors.Select(d => d.Specialty).Distinct().Order()) Targets.Add($"Any {s} doctor");
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var me = await CareUi.MyDoctorAsync(sync, session);
        var priority = new[] { ReferralPriority.Routine, ReferralPriority.Urgent, ReferralPriority.Emergency }[Math.Max(0, PriorityIndex)];
        var input = new ReferralInput(_patientId, sync.CurrentBranchId, Reason ?? "", priority, _encounterId, _caseId, me?.Id);
        LocalReferral saved;
        if (IsInternal)
        {
            if (TargetIndex < 0) throw new InvalidOperationException("Choose who to refer to.");
            var doctors = _doctors.OrderBy(d => d.FullName).ToList();
            saved = TargetIndex < doctors.Count
                ? await sync.Referrals.ReferInternalAsync(input, doctors[TargetIndex].Id, null)
                : await sync.Referrals.ReferInternalAsync(input, null, Targets[TargetIndex].Replace("Any ", "").Replace(" doctor", ""));
        }
        else if (IsExternal) saved = await sync.Referrals.ReferExternalAsync(input, Facility ?? "", ExternalDoctor, Summary);
        else saved = await sync.Referrals.RecordIncomingAsync(input, null, SourceName);
        _referralId = saved.Id;
        await LoadAsync();
    });

    [RelayCommand]
    private Task AcceptAsync() => RespondAsync(id => sync.Referrals.AcceptAsync(id));

    [RelayCommand]
    private async Task DeclineAsync()
    {
        var why = await Ui.PromptAsync("Decline", "Why is the referral declined?");
        if (!string.IsNullOrWhiteSpace(why)) await RespondAsync(id => sync.Referrals.DeclineAsync(id, why));
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        var note = await Ui.PromptAsync("Complete", "Outcome note for the referring doctor");
        if (note is not null) await RespondAsync(id => sync.Referrals.CompleteAsync(id, note));
    }

    private Task RespondAsync(Func<Guid, Task<LocalReferral>> action) => RunAsync(async () =>
    {
        if (_referralId is { } id) await action(id);
        await LoadAsync();
    });

    [RelayCommand]
    private Task PrintAsync() => RunAsync(async () =>
    {
        if (_referralId is not { } id) return;
        var letter = await sync.Referrals.BuildLetterAsync(id, CareUi.Clinic(session), CareUi.BranchLine(sync, session));
        await CareUi.OpenPdfAsync("Referral letter", ClinicLetterPdf.Render(letter));
    });
}

// =====================================================================================================
// §4.12 Procedure
// =====================================================================================================

/// <summary>FR-PRC-01/02: procedure note, consent with the signature pad, start, complete, print the consent.</summary>
public sealed partial class ProcedureViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid _procedureId;

    public ObservableCollection<string> Consumables { get; } = [];
    public ObservableCollection<string> Relations { get; } = ["Self", "Parent", "Guardian", "Spouse"];

    [ObservableProperty] public partial string Title { get; set; } = "Procedure";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial DynamicFormModel? NoteModel { get; set; }
    [ObservableProperty] public partial bool NeedsConsent { get; set; }
    [ObservableProperty] public partial bool HasConsent { get; set; }
    [ObservableProperty] public partial string? ConsentText { get; set; }
    [ObservableProperty] public partial string? ConsentTextUr { get; set; }
    [ObservableProperty] public partial string? ConsentSummary { get; set; }
    [ObservableProperty] public partial string? SignerName { get; set; }
    [ObservableProperty] public partial int RelationIndex { get; set; }
    [ObservableProperty] public partial string? Witness { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsPlanned { get; set; }

    public bool ShowConsentPad => NeedsConsent && !HasConsent && IsPlanned;
    partial void OnHasConsentChanged(bool value) => OnPropertyChanged(nameof(ShowConsentPad));
    partial void OnIsPlannedChanged(bool value) => OnPropertyChanged(nameof(ShowConsentPad));
    partial void OnNeedsConsentChanged(bool value) => OnPropertyChanged(nameof(ShowConsentPad));

    /// <summary>Set by the page: returns the signature pad drawing as PNG.</summary>
    public Func<Task<byte[]?>>? CaptureSignature { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = RunAsync(async () =>
    {
        if (query.TryGetValue("procedureId", out var p) && p is Guid id) _procedureId = id;
        else if (query.TryGetValue("patientId", out var pat) && pat is Guid patientId)
        {
            var created = await PlanNewAsync(patientId,
                query.TryGetValue("encounterId", out var e) && e is Guid eid ? eid : null,
                query.TryGetValue("caseId", out var c) && c is Guid cid ? cid : null);
            if (created is null) { await Ui.GoBackAsync(); return; }
            _procedureId = created.Id;
        }
        await ReloadAsync();
    });

    private async Task<LocalProcedure?> PlanNewAsync(Guid patientId, Guid? encounterId, Guid? caseId)
    {
        var types = await sync.Procedures.TypesAsync();
        var pick = await CareUi.PickAsync("Procedure", types.Select(t => t.Fee is { } f ? $"{t.Name} · Rs {f:N0}" : t.Name));
        var type = types.FirstOrDefault(t => (t.Fee is { } f ? $"{t.Name} · Rs {f:N0}" : t.Name) == pick);
        if (type is null) return null;
        var me = await CareUi.MyDoctorAsync(sync, session);
        var admission = await sync.DayCare.ActiveForPatientAsync(patientId);
        return await sync.Procedures.PlanAsync(patientId, sync.CurrentBranchId, type.Id, new ProcedureLinks(encounterId, caseId, admission?.Id, me?.Id));
    }

    private async Task ReloadAsync()
    {
        var p = await sync.Procedures.GetAsync(_procedureId) ?? throw new InvalidOperationException("Procedure not found.");
        var patient = await sync.Store.GetAsync<LocalPatient>(p.PatientId);
        var type = await sync.Store.GetAsync<LocalProcedureType>(p.ProcedureTypeId);
        Title = p.Name;
        Subtitle = $"{patient?.FullName}" + (p.Fee is { } f ? $" · Rs {f:N0}" : "");
        StatusText = ProcedureStatus.Display(p.Status);
        IsActive = p.Status is ProcedureStatus.Planned or ProcedureStatus.InProgress;
        IsPlanned = p.Status == ProcedureStatus.Planned;
        NeedsConsent = p.ConsentRequired;
        HasConsent = p.ConsentSignedAt is not null;
        ConsentText = type?.ConsentTextEn;
        ConsentTextUr = type?.ConsentTextUr;
        ConsentSummary = p.ConsentSignedAt is { } at ? $"✔ Consent signed by {p.ConsentSignerName} ({p.ConsentRelation}) {at.ToLocalTime():dd MMM HH:mm}" : null;
        SignerName ??= patient?.FullName;
        Consumables.Clear();
        foreach (var c in ProcedureService.Consumables(p.ConsumablesJson)) Consumables.Add($"{c.Name} × {c.Quantity:0.##} {c.Unit}".TrimEnd());

        if (await sync.Procedures.FormAsync(p) is { } form)
        {
            var values = await sync.Forms.LoadValuesAsync(SyncEntityNames.Procedure, p.Id);
            NoteModel = new DynamicFormModel(form, values, ProcedureService.ContextFor(p, session.Me?.Roles ?? []), await sync.Forms.LoadLookupsAsync(form.Definition), isNewRecord: IsActive);
        }
    }

    [RelayCommand]
    private Task SignConsentAsync() => RunAsync(async () =>
    {
        var png = CaptureSignature is null ? null : await CaptureSignature();
        if (png is null || png.Length == 0) throw new InvalidOperationException("Ask the patient to sign in the box.");
        var relation = new[] { ConsentRelation.Self, ConsentRelation.Parent, ConsentRelation.Guardian, ConsentRelation.Spouse }[Math.Max(0, RelationIndex)];
        await sync.Procedures.RecordConsentAsync(_procedureId, SignerName ?? "", relation, png, Witness);
        await ReloadAsync();
    });

    [RelayCommand]
    private Task SaveNoteAsync() => RunAsync(async () =>
    {
        if (NoteModel is { } m) await sync.Procedures.SaveNoteAsync(_procedureId, m.ValuesToSave());
        await ReloadAsync();
    });

    [RelayCommand]
    private Task StartAsync() => RunAsync(async () => { await sync.Procedures.StartAsync(_procedureId); await ReloadAsync(); });

    [RelayCommand]
    private Task CompleteAsync() => RunAsync(async () =>
    {
        if (NoteModel is { } m && !m.Validate()) throw new InvalidOperationException("Complete the procedure note first.");
        await sync.Procedures.CompleteAsync(_procedureId, NoteModel?.ValuesToSave());
        await ReloadAsync();
    });

    [RelayCommand]
    private async Task CancelProcedureAsync()
    {
        var reason = await Ui.PromptAsync("Cancel procedure", "Reason");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await RunAsync(async () => { await sync.Procedures.CancelAsync(_procedureId, reason); await ReloadAsync(); });
    }

    [RelayCommand]
    private Task PrintConsentAsync() => RunAsync(async () =>
    {
        var doc = await sync.Procedures.BuildConsentAsync(_procedureId, CareUi.Clinic(session), CareUi.BranchLine(sync, session), sync.Files);
        await CareUi.OpenPdfAsync("Consent form", ClinicLetterPdf.Render(doc));
    });
}

// =====================================================================================================
// §4.12 Day care
// =====================================================================================================

public sealed partial class DayCareViewModel(SyncCoordinator sync) : ViewModelBase
{
    public ObservableCollection<CareRow> Beds { get; } = [];

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        Beds.Clear();
        foreach (var b in await sync.DayCare.BoardAsync(sync.CurrentBranchId))
        {
            var label = $"{b.Bed.Code}" + (b.Bed.Ward is null ? "" : $" · {b.Bed.Ward}");
            var detail = b.Admission is null
                ? $"Free · {b.Bed.Kind} · Rs {b.Bed.ChargeAmount:N0}/{b.Bed.ChargeUnit}"
                : $"{b.PatientName} · since {b.Admission.AdmittedAt.ToLocalTime():dd MMM HH:mm}" + (b.Conflict ? " · ⚠ two patients recorded in this bed" : "");
            Beds.Add(new CareRow(b.Bed.Id, label, detail, b.Conflict, b));
        }
    });

    [RelayCommand]
    private async Task OpenAsync(CareRow row)
    {
        if (row.Tag is not BedOccupancy b) return;
        if (b.Admission is { } a) { await Ui.GoToAsync(Routes.Admission, new Dictionary<string, object> { ["admissionId"] = a.Id }); return; }
        await Ui.AlertAsync("Admit", "Open the patient and choose \"Admit to day care\" to use this bed.");
    }
}

/// <summary>FR-PRC-03/04: one stay — transfer, nursing notes, medication chart, running room charges, discharge.</summary>
public sealed partial class AdmissionViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid _admissionId;

    public ObservableCollection<CareRow> Notes { get; } = [];
    public ObservableCollection<CareRow> Chart { get; } = [];
    public ObservableCollection<string> Charges { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "Day care";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string? Warning { get; set; }
    [ObservableProperty] public partial string ChargesTotal { get; set; } = "";
    [ObservableProperty] public partial bool IsAdmitted { get; set; }
    [ObservableProperty] public partial string? NewNote { get; set; }

    public bool CanOrder => IsAdmitted && session.Has(Permissions.Prescriptions.Create);
    public bool CanChart => IsAdmitted && session.Has(Permissions.Nursing.Record);
    public bool CanManage => IsAdmitted && session.Has(Permissions.Admissions.Manage);
    partial void OnIsAdmittedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOrder));
        OnPropertyChanged(nameof(CanChart));
        OnPropertyChanged(nameof(CanManage));
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = RunAsync(async () =>
    {
        if (query.TryGetValue("admissionId", out var a) && a is Guid id) _admissionId = id;
        else if (query.TryGetValue("patientId", out var p) && p is Guid patientId)
        {
            var admitted = await AdmitAsync(patientId);
            if (admitted is null) { await Ui.GoBackAsync(); return; }
            _admissionId = admitted.Id;
        }
        await ReloadAsync();
    });

    private async Task<LocalAdmission?> AdmitAsync(Guid patientId)
    {
        if (await sync.DayCare.ActiveForPatientAsync(patientId) is { } existing) return existing;
        var free = (await sync.DayCare.BoardAsync(sync.CurrentBranchId)).Where(b => b.Admission is null).ToList();
        if (free.Count == 0) { await Ui.AlertAsync("Day care", "No free bed at this branch."); return null; }
        var pick = await CareUi.PickAsync("Bed", free.Select(b => $"{b.Bed.Code} · Rs {b.Bed.ChargeAmount:N0}/{b.Bed.ChargeUnit}"));
        var bed = free.FirstOrDefault(b => $"{b.Bed.Code} · Rs {b.Bed.ChargeAmount:N0}/{b.Bed.ChargeUnit}" == pick);
        if (bed is null) return null;
        var reason = await Ui.PromptAsync("Admit", "Reason for admission");
        if (reason is null) return null;
        var me = await CareUi.MyDoctorAsync(sync, session);
        var openCase = (await sync.Cases.ForPatientAsync(patientId, openOnly: true)).FirstOrDefault();
        return await sync.DayCare.AdmitAsync(patientId, sync.CurrentBranchId, bed.Bed.Id, reason, me?.Id, openCase?.Id);
    }

    private async Task ReloadAsync()
    {
        var a = await sync.DayCare.GetAsync(_admissionId) ?? throw new InvalidOperationException("Admission not found.");
        var patient = await sync.Store.GetAsync<LocalPatient>(a.PatientId);
        var beds = (await sync.Store.ListAsync<LocalBed>()).ToDictionary(b => b.Id, b => b.Code);
        Title = patient?.FullName ?? "Day care";
        IsAdmitted = a.Status == AdmissionStatus.Admitted;
        Subtitle = $"Bed {beds.GetValueOrDefault(a.BedId, "?")} · admitted {a.AdmittedAt.ToLocalTime():dd MMM HH:mm}" +
                   (a.DischargedAt is { } d ? $" · discharged {d.ToLocalTime():dd MMM HH:mm}" : "") + (a.Reason is null ? "" : $"\n{a.Reason}");
        Warning = a.BedConflict ? "⚠ Another patient was admitted to this bed on a different device. Transfer one of them." : null;

        var charges = await sync.DayCare.ChargesAsync(a.Id);
        Charges.Clear();
        foreach (var l in charges.Lines) Charges.Add($"{beds.GetValueOrDefault(l.BedId, "?")}: {l.Units} {l.Unit}(s) × Rs {l.Rate:N0} = Rs {l.Amount:N0}");
        ChargesTotal = $"Room charges so far: Rs {charges.Total:N0}";

        Notes.Clear();
        foreach (var n in await sync.DayCare.NursingNotesAsync(a.Id)) Notes.Add(new CareRow(n.Id, n.Body, $"{n.Kind} · {n.AuthorName} · {n.WrittenAt.ToLocalTime():dd MMM HH:mm}"));
        Chart.Clear();
        foreach (var r in await sync.DayCare.ChartAsync(a.Id))
        {
            var o = r.Order;
            var last = r.LastDose is { } ld ? $" · last {ld.Status} {ld.GivenAt.ToLocalTime():HH:mm}" : " · not given yet";
            Chart.Add(new CareRow(o.Id, $"{o.DrugName} {o.Dose}".Trim(),
                $"{Frequencies.Find(o.Frequency)?.English ?? o.Frequency} · {o.Route} · {o.Status}{last} ({r.Doses.Count} charted)", o.Status == MedicationOrderStatus.Stopped, o));
        }
    }

    [RelayCommand]
    private Task AddNoteAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewNote)) return;
        await sync.DayCare.AddNursingNoteAsync(_admissionId, NewNote, NursingNoteKind.Note, session.Me?.FullName);
        NewNote = null;
        await ReloadAsync();
    });

    [RelayCommand]
    private async Task OrderAsync()
    {
        var name = await Ui.PromptAsync("Medication order", "Medicine (as on the chart)");
        if (string.IsNullOrWhiteSpace(name)) return;
        var dose = await Ui.PromptAsync("Dose", "e.g. 4 mg");
        var route = await CareUi.PickAsync("Route", DrugRoutes.All);
        var freq = await CareUi.PickAsync("Frequency", Frequencies.All.Select(f => f.English));
        var code = Frequencies.All.FirstOrDefault(f => f.English == freq)?.Code;
        await RunAsync(async () => { await sync.DayCare.OrderMedicationAsync(_admissionId, name, dose, route, code); await ReloadAsync(); });
    }

    [RelayCommand]
    private async Task ChartActionAsync(CareRow row)
    {
        if (row.Tag is not LocalMedicationOrder o || o.Status != MedicationOrderStatus.Active) return;
        var options = new List<string>();
        if (CanChart) options.AddRange(["Given now", "Held", "Refused"]);
        if (CanOrder || CanManage) options.Add("Stop medicine");
        var pick = await CareUi.PickAsync(o.DrugName, options);
        if (pick is null) return;
        await RunAsync(async () =>
        {
            if (pick == "Stop medicine") await sync.DayCare.StopMedicationAsync(o.Id);
            else if (pick == "Given now") await sync.DayCare.RecordDoseAsync(o.Id);
            else await sync.DayCare.RecordDoseAsync(o.Id, pick == "Held" ? MedicationDoseStatus.Held : MedicationDoseStatus.Refused, await Ui.PromptAsync(pick, "Why?"));
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task TransferAsync()
    {
        var free = (await sync.DayCare.BoardAsync(sync.CurrentBranchId)).Where(b => b.Admission is null).ToList();
        var pick = await CareUi.PickAsync("Move to bed", free.Select(b => b.Bed.Code));
        var bed = free.FirstOrDefault(b => b.Bed.Code == pick);
        if (bed is null) return;
        await RunAsync(async () => { await sync.DayCare.TransferAsync(_admissionId, bed.Bed.Id, await Ui.PromptAsync("Transfer", "Reason (optional)")); await ReloadAsync(); });
    }

    [RelayCommand]
    private async Task DischargeAsync()
    {
        var summary = await Shell.Current.DisplayPromptAsync("Discharge", "Discharge summary", "Next", "Cancel", maxLength: 4000);
        if (string.IsNullOrWhiteSpace(summary)) return;
        var advice = await Shell.Current.DisplayPromptAsync("Discharge", "Advice for the patient (optional)", "Discharge", "Cancel", maxLength: 2000);
        await RunAsync(async () =>
        {
            await sync.DayCare.DischargeAsync(_admissionId, summary, advice);
            await ReloadAsync();
        });
        if (!IsAdmitted && await Ui.ConfirmAsync("Discharged", "Print the discharge summary?", "Print")) await PrintAsync();
    }

    [RelayCommand]
    private Task PrintAsync() => RunAsync(async () =>
    {
        var doc = await sync.DayCare.BuildDischargeSummaryAsync(_admissionId, CareUi.Clinic(session), CareUi.BranchLine(sync, session));
        await CareUi.OpenPdfAsync("Discharge summary", ClinicLetterPdf.Render(doc));
    });
}

// =====================================================================================================
// FR-REF-04 follow-ups and FR-REF-01 referral inbox
// =====================================================================================================

public sealed partial class FollowUpsViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase
{
    public ObservableCollection<CareRow> FollowUps { get; } = [];
    public ObservableCollection<CareRow> Referrals { get; } = [];
    public ObservableCollection<CareRow> MyCases { get; } = [];

    [ObservableProperty] public partial bool OnlyMine { get; set; } = true;
    [ObservableProperty] public partial int DaysAhead { get; set; } = 7;
    [ObservableProperty] public partial bool IsDoctor { get; set; }
    partial void OnOnlyMineChanged(bool value) => LoadCommand.Execute(null);

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var me = await CareUi.MyDoctorAsync(sync, session);
        IsDoctor = me is not null;
        var today = DateTime.Today;
        FollowUps.Clear();
        foreach (var f in await sync.FollowUps.DueAsync(today.AddDays(DaysAhead), OnlyMine ? me?.Id : null, branchId: sync.CurrentBranchId))
        {
            var overdue = f.DaysOverdue(today);
            FollowUps.Add(new CareRow(f.PatientId, f.PatientName,
                $"{f.Reason} · {(overdue > 0 ? $"{overdue} day(s) overdue" : $"due {f.DueDate:ddd dd MMM}")}" + (f.CaseTitle is null ? "" : $" · {f.CaseTitle}") +
                (f.DoctorName is null ? "" : $" · {f.DoctorName}"), overdue > 0, f));
        }

        Referrals.Clear();
        MyCases.Clear();
        if (me is null) return;
        var patients = (await sync.Store.ListAsync<LocalPatient>()).ToDictionary(p => p.Id, p => p.FullName);
        foreach (var r in await sync.Referrals.InboxAsync(me.Id))
            Referrals.Add(new CareRow(r.Id, $"{patients.GetValueOrDefault(r.PatientId)} · {r.Priority}", $"{r.Status} · {r.Reason}", r.Priority != ReferralPriority.Routine));
        foreach (var c in await sync.Cases.OpenForDoctorAsync(me.Id))
            MyCases.Add(new CareRow(c.Id, c.Title, $"{patients.GetValueOrDefault(c.PatientId)} · {CaseStatus.Display(c.Status)}"));
    });

    [RelayCommand]
    private async Task ContactAsync(CareRow row)
    {
        if (row.Tag is not FollowUpItem f) return;
        var options = new List<string> { "Call", "SMS", "WhatsApp" };
        if (session.Has(Permissions.Messages.Send)) options.Insert(0, "Queue reminder (sent by the server)");
        options.Add("Open patient");
        var pick = await CareUi.PickAsync(f.PatientName, options);
        if (pick?.StartsWith("Queue") == true)
        {
            await EngagementUi.SendMessageAsync(sync, session, f.PatientId, MessageTemplateKeys.FollowUp,
                new Dictionary<string, string?> { ["date"] = f.DueDate.ToString("dd MMM yyyy"), ["doctor"] = f.DoctorName });
            return;
        }
        var message = FollowUpService.ReminderMessage(f, CareUi.Clinic(session));
        var uri = pick switch
        {
            "Call" => ContactLinks.Tel(f.Phone),
            "SMS" => ContactLinks.Sms(f.Phone) is { } s ? $"{s}?body={Uri.EscapeDataString(message)}" : null,
            "WhatsApp" => ContactLinks.WhatsApp(f.Phone, message),
            _ => null,
        };
        if (pick == "Open patient") { await Ui.GoToAsync(Routes.Patient, new Dictionary<string, object> { ["patientId"] = f.PatientId }); return; }
        if (pick is null) return;
        if (uri is null) { await Ui.AlertAsync("Contact", "This patient has no phone number."); return; }
        try { await Launcher.Default.OpenAsync(uri); }
        catch (Exception ex) { await Ui.AlertAsync("Contact", ex.Message); }
    }

    [RelayCommand]
    private Task OpenReferralAsync(CareRow row) => Ui.GoToAsync(Routes.Referral, new Dictionary<string, object> { ["referralId"] = row.Id });

    [RelayCommand]
    private Task OpenCaseAsync(CareRow row) => Ui.GoToAsync(Routes.Case, new Dictionary<string, object> { ["caseId"] = row.Id });
}

// =====================================================================================================
// Admin: case types, procedures, beds (online)
// =====================================================================================================

public sealed partial class CareSetupViewModel(ApiClient api, SessionStore session) : ViewModelBase
{
    public ObservableCollection<CareRow> CaseTypes { get; } = [];
    public ObservableCollection<CareRow> Procedures { get; } = [];
    public ObservableCollection<CareRow> Beds { get; } = [];
    public ObservableCollection<CareRow> LabTests { get; } = [];
    public ObservableCollection<CareRow> Services { get; } = [];

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        LabTests.Clear();
        foreach (var t in await api.GetAsync<List<LabTestDto>>(LabBillingRoutes.LabTests))
            LabTests.Add(new CareRow(t.Id, t.Name + (t.IsActive ? "" : " (inactive)"), $"{t.Code} · {t.Department} · {t.SampleType} · Rs {t.Fee:N0} · {t.TurnaroundHours} h", Tag: t));
        Services.Clear();
        var branchNames = session.Me?.Branches.ToDictionary(b => b.Id, b => b.Name) ?? [];
        foreach (var s in await api.GetAsync<List<ServiceItemDto>>(LabBillingRoutes.Services))
            Services.Add(new CareRow(s.Id, s.Name + (s.IsActive ? "" : " (inactive)"),
                $"{s.Code} · {s.Kind} · Rs {s.Price:N0}" + (s.TaxPercent > 0 ? $" + {s.TaxPercent:0.#}% tax" : "") +
                (s.BranchId is { } b ? $" · {branchNames.GetValueOrDefault(b, "branch")} only" : " · all branches"), Tag: s));

        CaseTypes.Clear();
        foreach (var t in await api.GetAsync<List<CaseTypeDto>>(CareRoutes.CaseTypes))
            CaseTypes.Add(new CareRow(t.Id, t.Name + (t.IsActive ? "" : " (inactive)"),
                $"{t.Code} · {t.ExpectedSessions?.ToString() ?? "–"} sessions · every {t.FollowUpIntervalDays?.ToString() ?? "–"} days · {t.DefaultPlan.Count} plan steps", Tag: t));
        Procedures.Clear();
        foreach (var p in await api.GetAsync<List<ProcedureTypeDto>>(CareRoutes.ProcedureTypes))
            Procedures.Add(new CareRow(p.Id, p.Name + (p.IsActive ? "" : " (inactive)"),
                $"{p.Category} · Rs {p.Fee:N0} · {p.DurationMinutes} min" + (p.RequiresConsent ? " · consent" : "") + $" · {p.Consumables.Count} consumables", Tag: p));
        Beds.Clear();
        var branches = session.Me?.Branches.ToDictionary(b => b.Id, b => b.Name) ?? [];
        foreach (var b in await api.GetAsync<List<BedDto>>(CareRoutes.Beds))
            Beds.Add(new CareRow(b.Id, $"{b.Code} · {b.Ward}", $"{branches.GetValueOrDefault(b.BranchId, "branch")} · {b.Kind} · Rs {b.ChargeAmount:N0}/{b.ChargeUnit}" + (b.IsActive ? "" : " · out of use"), Tag: b));
    });

    [RelayCommand]
    private async Task AddCaseTypeAsync()
    {
        var name = await Ui.PromptAsync("New case type", "Name, e.g. \"Knee physiotherapy\"");
        if (string.IsNullOrWhiteSpace(name)) return;
        var sessions = await Shell.Current.DisplayPromptAsync("Expected sessions", "Number of sessions (blank if open-ended)", "OK", "Cancel", keyboard: Keyboard.Numeric);
        var interval = await Shell.Current.DisplayPromptAsync("Follow-up interval", "Days between visits", "OK", "Cancel", initialValue: "14", keyboard: Keyboard.Numeric);
        var code = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        await RunAsync(async () =>
        {
            await api.PostAsync<CaseTypeDto>(CareRoutes.CaseTypes, new SaveCaseTypeRequest(code[..Math.Min(40, code.Length)], name, null, null,
                int.TryParse(sessions, out var s) ? s : null, int.TryParse(interval, out var i) ? i : null, null, []));
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task ToggleCaseTypeAsync(CareRow row)
    {
        if (row.Tag is not CaseTypeDto t || !await Ui.ConfirmAsync(t.Name, t.IsActive ? "Stop offering this case type?" : "Offer this case type again?")) return;
        await RunAsync(async () =>
        {
            await api.PutAsync<CaseTypeDto>($"{CareRoutes.CaseTypes}/{t.Id}", new SaveCaseTypeRequest(t.Code, t.Name, t.NameUr, t.Specialty,
                t.ExpectedSessions, t.FollowUpIntervalDays, t.DefaultServices, t.DefaultPlan, !t.IsActive));
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task EditProcedureAsync(CareRow row)
    {
        if (row.Tag is not ProcedureTypeDto p) return;
        var fee = await Shell.Current.DisplayPromptAsync(p.Name, "Fee (Rs)", "Save", "Cancel", initialValue: p.Fee?.ToString("0") ?? "", keyboard: Keyboard.Numeric);
        if (fee is null) return;
        await RunAsync(async () =>
        {
            await api.PutAsync<ProcedureTypeDto>($"{CareRoutes.ProcedureTypes}/{p.Id}", new SaveProcedureTypeRequest(p.Code, p.Name, p.Category,
                decimal.TryParse(fee, out var f) ? f : p.Fee, p.DurationMinutes, p.RequiresConsent, p.ConsentTextEn, p.ConsentTextUr, p.Consumables, p.IsActive));
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task EditLabTestAsync(CareRow row)
    {
        if (row.Tag is not LabTestDto t) return;
        var fee = await Shell.Current.DisplayPromptAsync(t.Name, "Fee (Rs)", "Save", "Cancel", initialValue: t.Fee?.ToString("0") ?? "", keyboard: Keyboard.Numeric);
        if (fee is null) return;
        await RunAsync(async () =>
        {
            await api.PutAsync<LabTestDto>($"{LabBillingRoutes.LabTests}/{t.Id}",
                new SaveLabTestRequest(t.Code, t.Name, t.Department, t.SampleType, decimal.TryParse(fee, out var f) ? f : t.Fee, t.TurnaroundHours, t.IsActive));
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task AddServiceAsync()
    {
        var name = await Ui.PromptAsync("New service", "Name, e.g. \"Ear syringing\"");
        if (string.IsNullOrWhiteSpace(name)) return;
        var kind = await CareUi.PickAsync("Kind", ServiceKind.All.Order());
        var price = await Shell.Current.DisplayPromptAsync("Price", "Rs", "OK", "Cancel", keyboard: Keyboard.Numeric);
        if (kind is null || !decimal.TryParse(price, out var p)) return;
        var code = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        await RunAsync(async () =>
        {
            await api.PostAsync<ServiceItemDto>(LabBillingRoutes.Services, new SaveServiceItemRequest(code[..Math.Min(40, code.Length)], name, kind, null, p));
            await LoadAsync();
        });
    }

    /// <summary>FR-BIL-01: change the price, or set a different price for one branch.</summary>
    [RelayCommand]
    private async Task EditServiceAsync(CareRow row)
    {
        if (row.Tag is not ServiceItemDto s) return;
        var branches = session.Me?.Branches ?? [];
        var options = new List<string> { "Change price" };
        if (s.BranchId is null && branches.Count > 0) options.Add("Different price for a branch…");
        options.Add(s.IsActive ? "Stop offering" : "Offer again");
        var pick = await CareUi.PickAsync(s.Name, options);
        if (pick is null) return;
        Guid? branchId = s.BranchId;
        Guid? id = s.Id;
        var active = s.IsActive;
        var price = s.Price;
        if (pick.StartsWith("Different"))
        {
            var name = await CareUi.PickAsync("Branch", branches.Select(b => b.Name));
            branchId = branches.FirstOrDefault(b => b.Name == name)?.Id;
            if (branchId is null) return;
            id = null;
        }
        if (pick.StartsWith("Stop") || pick.StartsWith("Offer")) active = !s.IsActive;
        else
        {
            var text = await Shell.Current.DisplayPromptAsync(s.Name, "Price (Rs)", "Save", "Cancel", initialValue: s.Price.ToString("0"), keyboard: Keyboard.Numeric);
            if (!decimal.TryParse(text, out price)) return;
        }
        var request = new SaveServiceItemRequest(s.Code, s.Name, s.Kind, branchId, price, s.TaxPercent, active);
        await RunAsync(async () =>
        {
            if (id is { } existing) await api.PutAsync<ServiceItemDto>($"{LabBillingRoutes.Services}/{existing}", request);
            else await api.PostAsync<ServiceItemDto>(LabBillingRoutes.Services, request);
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task AddBedAsync()
    {
        var branches = session.Me?.Branches ?? [];
        var branchName = branches.Count == 1 ? branches[0].Name : await CareUi.PickAsync("Branch", branches.Select(b => b.Name));
        var branch = branches.FirstOrDefault(b => b.Name == branchName);
        if (branch is null) return;
        var code = await Ui.PromptAsync("New bed", "Code, e.g. DC-1");
        if (string.IsNullOrWhiteSpace(code)) return;
        var ward = await Ui.PromptAsync("Ward / room", "e.g. Day care");
        var kind = await CareUi.PickAsync("Kind", ["bed", "room", "chair"]);
        var unit = await CareUi.PickAsync("Charged per", ["day", "hour"]);
        var amount = await Shell.Current.DisplayPromptAsync("Charge", $"Rs per {unit}", "OK", "Cancel", initialValue: "0", keyboard: Keyboard.Numeric);
        if (kind is null || unit is null || !decimal.TryParse(amount, out var a)) return;
        await RunAsync(async () =>
        {
            await api.PostAsync<BedDto>(CareRoutes.Beds, new SaveBedRequest(branch.Id, code, ward, kind, a, unit));
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task ToggleBedAsync(CareRow row)
    {
        if (row.Tag is not BedDto b || !await Ui.ConfirmAsync(b.Code, b.IsActive ? "Take this bed out of use?" : "Put this bed back in use?")) return;
        await RunAsync(async () =>
        {
            await api.PutAsync<BedDto>($"{CareRoutes.Beds}/{b.Id}", new SaveBedRequest(b.BranchId, b.Code, b.Ward, b.Kind, b.ChargeAmount, b.ChargeUnit, !b.IsActive));
            await LoadAsync();
        });
    }
}

/// <summary>
/// FR-AI-21 review step: OCR proposals are listed with a tick each (high-confidence ones ticked); the person toggles
/// them and applies. Nothing reaches the form unless it was ticked (§13: AI suggests, a person decides).
/// </summary>
internal static class OcrReview
{
    public static async Task<IReadOnlyList<OcrProposal>> ChooseAsync(IReadOnlyList<OcrProposal> proposals)
    {
        if (proposals.Count == 0)
        {
            await Ui.AlertAsync("Scan", "No values for this form were found in the document.");
            return [];
        }
        var ticked = proposals.Select(p => p.Confidence >= 0.8).ToArray();
        while (true)
        {
            var options = proposals.Select((p, i) => $"{(ticked[i] ? "☑" : "☐")} {p.Label}: {p.Display}").ToArray();
            var pick = await Shell.Current.DisplayActionSheetAsync("Values read from the document — tap to tick or untick", "Cancel", $"Apply {ticked.Count(t => t)} ticked", options);
            if (pick is null or "Cancel") return [];
            if (pick.StartsWith("Apply ")) return proposals.Where((_, i) => ticked[i]).ToList();
            var index = Array.IndexOf(options, pick);
            if (index >= 0) ticked[index] = !ticked[index];
        }
    }

    /// <summary>Scans a document and applies the ticked values to a dynamic form.</summary>
    public static async Task<int> ScanIntoAsync(DocumentScanner scanner, DynamicFormModel model)
    {
        var text = await scanner.ScanAsync();
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var chosen = await ChooseAsync(OcrExtraction.ProposeForForm(text, model.Definition));
        foreach (var p in chosen) model.SetValue(p.FieldKey, p.Value);
        return chosen.Count;
    }
}
