using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Clinic.App.Services;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record RxRow(LocalPrescriptionItem Item)
{
    public string Title => $"{Item.DrugName} {Item.Strength}".Trim();
    public string Detail => string.Join(" · ", new[]
    {
        Item.Dose, Frequencies.Find(Item.Frequency)?.English ?? Item.Frequency,
        Item.DurationDays is { } d && d > 0 ? $"{d} days" : null, Item.Instructions,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string? Urdu => Frequencies.Find(Item.Frequency)?.Urdu;
}

public sealed record DiagnosisRow(LocalDiagnosis Diagnosis)
{
    public string Text => (Diagnosis.Code is null ? "" : Diagnosis.Code + " — ") + Diagnosis.Description + (Diagnosis.IsPrimary ? "  (primary)" : "");
}

/// <summary>
/// §4.3/4.4: the consultation screen — vitals, the specialty form, diagnoses, prescription, sign, amend and print.
/// Everything works offline; printing renders the PDF on the device.
/// </summary>
public sealed partial class EncounterViewModel(SyncCoordinator sync, SessionStore session, ReadAloudService voice, DocumentScanner scanner)
    : ViewModelBase, IQueryAttributable
{
    private Guid _encounterId;
    private LocalEncounter? _encounter;
    private FormVersionInfo? _vitalsVersion;
    private IDispatcherTimer? _autosave;
    private DateTime? _savedFollowUp;

    public ObservableCollection<DiagnosisRow> Diagnoses { get; } = [];
    public ObservableCollection<LocalIcd10> IcdResults { get; } = [];
    public ObservableCollection<RxRow> Items { get; } = [];
    public ObservableCollection<LocalDrug> DrugResults { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> AmendmentLog { get; } = [];

    [ObservableProperty] public partial string PatientName { get; set; } = "";
    [ObservableProperty] public partial string PatientDetails { get; set; } = "";
    [ObservableProperty] public partial string? Allergies { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsLocked { get; set; }
    [ObservableProperty] public partial DynamicFormModel? NotesModel { get; set; }
    [ObservableProperty] public partial DynamicFormModel? VitalsModel { get; set; }
    [ObservableProperty] public partial string? LatestVitals { get; set; }
    [ObservableProperty] public partial string? IcdQuery { get; set; }
    [ObservableProperty] public partial string? DrugQuery { get; set; }
    [ObservableProperty] public partial DateTime? FollowUp { get; set; }
    [ObservableProperty] public partial bool HasWarnings { get; set; }
    [ObservableProperty] public partial bool HasAmendments { get; set; }
    [ObservableProperty] public partial string CaseText { get; set; } = "Not part of a case";

    /// <summary>Procedures done in this visit (§4.12).</summary>
    public ObservableCollection<CareRow> Procedures { get; } = [];
    public bool CanUseCases => session.Has(Permissions.Cases.Manage);
    public bool CanRefer => session.Has(Permissions.Referrals.Manage);
    public bool CanDoProcedures => session.Has(Permissions.Procedures.Perform);
    public bool CanScan => scanner.IsSupported;
    public bool CanOrderLabs => session.Has(Permissions.Labs.Order);

    /// <summary>Tests ordered in this visit with their status (§4.5).</summary>
    public ObservableCollection<CareRow> LabTests { get; } = [];

    [RelayCommand]
    private async Task OrderTestsAsync()
    {
        if (_encounter is not { } e) return;
        await RunAsync(async () => await LabUi.OrderAsync(sync, session, e.PatientId, e.Id));
        await RefreshCareAsync();
    }

    [RelayCommand]
    private Task LabReportAsync(CareRow row) => RunAsync(() => LabUi.PrintReportAsync(sync, session, row.Id));

    public bool CanSign => !IsLocked && session.Has(Permissions.Encounters.Sign);
    public string NotesButtonText => IsLocked ? "Amend notes (needs a reason)" : "Save notes";

    partial void OnIcdQueryChanged(string? value) => _ = SearchIcdAsync();
    partial void OnDrugQueryChanged(string? value) => _ = SearchDrugsAsync();
    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSign));
        OnPropertyChanged(nameof(NotesButtonText));
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = OpenAsync(query);

    private Task OpenAsync(IDictionary<string, object> query) => RunAsync(async () =>
    {
        if (query.TryGetValue("appointmentId", out var a) && a is Guid appointmentId)
            _encounterId = (await sync.Encounters.StartFromAppointmentAsync(appointmentId)).Id;
        else if (query.TryGetValue("encounterId", out var e) && e is Guid encounterId)
            _encounterId = encounterId;
        await ReloadAsync();
    });

    private async Task ReloadAsync()
    {
        _encounter = await sync.Encounters.GetAsync(_encounterId) ?? throw new InvalidOperationException("Encounter not found.");
        var e = _encounter;
        IsLocked = EncounterStatus.IsLocked(e.Status);
        StatusText = $"{EncounterStatus.Display(e.Status)}" + (e.SignedAt is { } s ? $" · signed {s.ToLocalTime():g}" : "");
        FollowUp = e.FollowUpDate;

        var patient = await sync.Store.GetAsync<LocalPatient>(e.PatientId);
        PatientName = patient?.FullName ?? "";
        PatientDetails = patient is null ? "" : new PatientRow(patient).Subtitle;
        Allergies = null;
        if (patient?.DynamicData is { Length: > 0 } dd && JsonNode.Parse(dd) is JsonObject values)
        {
            var list = FormValues.AsStringList(values["allergies"]).Where(x => x != "none").ToList();
            var notes = FormValues.AsString(values["allergy_notes"]);
            if (list.Count > 0 || notes is not null) Allergies = "⚠ Allergies: " + string.Join(", ", list.Append(notes).Where(x => x is not null));
        }

        var context = EncounterService.ContextFor(e, session.Me?.Roles ?? []);
        if (await sync.Encounters.FormAsync(e) is { } form)
        {
            var formValues = await sync.Forms.LoadValuesAsync(SyncEntityNames.Encounter, e.Id);
            NotesModel = new DynamicFormModel(form, formValues, context, await sync.Forms.LoadLookupsAsync(form.Definition), isNewRecord: !IsLocked);
        }

        _vitalsVersion = await sync.Forms.ResolveFormAsync(SyncEntityNames.Vitals, e.BranchId);
        VitalsModel = _vitalsVersion is null || IsLocked ? null : new DynamicFormModel(_vitalsVersion, [], context);
        LatestVitals = await VitalsSummaryAsync(e.PatientId);

        await RefreshListsAsync();
        await RefreshCareAsync();
        _savedFollowUp = FollowUp;
        StartAutosave();
    }

    /// <summary>
    /// FR-DYN-23: the consultation notes save themselves every 10 s and when leaving the page, so a crash, a flat
    /// battery or a closed app loses nothing. Signed encounters are never autosaved (changes need a reason).
    /// </summary>
    private void StartAutosave()
    {
        if (_autosave is not null || Application.Current?.Dispatcher is not { } dispatcher) return;
        _autosave = dispatcher.CreateTimer();
        _autosave.Interval = TimeSpan.FromSeconds(10);
        _autosave.Tick += async (_, _) => await AutosaveAsync();
        _autosave.Start();
    }

    public async Task AutosaveAsync()
    {
        if (IsLocked || IsBusy || !sync.IsStarted || NotesModel is not { } m) return;
        if (!m.IsDirty && FollowUp == _savedFollowUp) return;
        try
        {
            await sync.Encounters.SaveNotesAsync(_encounterId, m.ValuesToSave(), FollowUp);
            _savedFollowUp = FollowUp;
            // Keep editing the same model; mark it clean by rebuilding from what was saved.
            NotesModel = new DynamicFormModel(m.Version, m.ValuesToSave(), m.Context, null, isNewRecord: true);
        }
        catch (EncounterException) { /* signed meanwhile */ }
    }

    public async Task OnLeavingAsync()
    {
        _autosave?.Stop();
        _autosave = null;
        await AutosaveAsync();
    }

    private async Task<string?> VitalsSummaryAsync(Guid patientId)
    {
        if (await sync.Encounters.LatestVitalsAsync(patientId) is not { } latest || latest.Record.FormVersionId is not { } v ||
            await sync.Forms.GetVersionAsync(v) is not { } form) return null;
        var parts = form.Definition.AllFields.Where(f => !FormValues.IsEmpty(latest.Values[f.Key]))
            .Select(f => $"{f.Label.En} {FormValues.AsString(latest.Values[f.Key])}{f.Unit}");
        return $"Last vitals {latest.Record.RecordedAt.ToLocalTime():t}: " + string.Join(" · ", parts);
    }

    private async Task RefreshListsAsync()
    {
        Diagnoses.Clear();
        foreach (var d in await sync.Encounters.DiagnosesAsync(_encounterId)) Diagnoses.Add(new DiagnosisRow(d));
        Items.Clear();
        foreach (var i in await sync.Prescriptions.ItemsAsync(_encounterId)) Items.Add(new RxRow(i));
        Warnings.Clear();
        foreach (var w in await sync.Prescriptions.WarningsAsync(_encounterId)) Warnings.Add((w.Critical ? "⛔ " : "⚠ ") + w.Message);
        HasWarnings = Warnings.Count > 0;
        AmendmentLog.Clear();
        foreach (var a in await sync.Encounters.AmendmentsAsync(_encounterId))
            AmendmentLog.Add($"{a.AmendedAt.ToLocalTime():g} · {a.FieldKey}: {a.Reason}");
        HasAmendments = AmendmentLog.Count > 0;
    }

    /// <summary>On a signed encounter every change needs a reason (FR-ENC-05); returns null to cancel.</summary>
    private async Task<(bool Ok, string? Reason)> ReasonIfLockedAsync()
    {
        if (!IsLocked) return (true, null);
        var reason = await Ui.PromptAsync("Amend signed encounter", "Reason for this change");
        return string.IsNullOrWhiteSpace(reason) ? (false, null) : (true, reason);
    }

    // ---- notes and vitals

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        if (NotesModel is not { } m) return;
        if (IsLocked)
        {
            var (ok, reason) = await ReasonIfLockedAsync();
            if (!ok) return;
            if (await RunAsync(async () =>
                {
                    var count = await sync.Encounters.AmendNotesAsync(_encounterId, m.ValuesToSave(), reason!);
                    if (count == 0) throw new InvalidOperationException("Nothing changed.");
                }))
                await ReloadAsync();
            return;
        }
        if (await RunAsync(() => sync.Encounters.SaveNotesAsync(_encounterId, m.ValuesToSave(), FollowUp)))
            await Ui.AlertAsync("Saved", "Notes saved on this device.");
    }

    [RelayCommand]
    private async Task RecordVitalsAsync()
    {
        if (VitalsModel is not { } m || _encounter is null) return;
        if (!m.Validate()) { ErrorMessage = "Check the vitals."; return; }
        if (await RunAsync(() => sync.Encounters.RecordVitalsAsync(_encounter.PatientId, _encounter.BranchId, _encounter.Id, m.ValuesToSave())))
        {
            LatestVitals = await VitalsSummaryAsync(_encounter.PatientId);
            VitalsModel = new DynamicFormModel(_vitalsVersion!, [], EncounterService.ContextFor(_encounter, session.Me?.Roles ?? []));
        }
    }

    // ---- diagnoses

    private async Task SearchIcdAsync()
    {
        IcdResults.Clear();
        if (string.IsNullOrWhiteSpace(IcdQuery) || IcdQuery.Length < 2) return;
        foreach (var c in await sync.Encounters.SearchIcdAsync(IcdQuery, 12)) IcdResults.Add(c);
    }

    [RelayCommand]
    private async Task AddIcdAsync(LocalIcd10 code)
    {
        var (ok, reason) = await ReasonIfLockedAsync();
        if (!ok) return;
        if (await RunAsync(() => sync.Encounters.AddDiagnosisAsync(_encounterId, code.Code, code.Description, Diagnoses.Count == 0, reason)))
        {
            IcdQuery = null;
            await RefreshListsAsync();
        }
    }

    [RelayCommand]
    private async Task AddFreeTextDiagnosisAsync()
    {
        var text = await Ui.PromptAsync("Diagnosis", "Free-text diagnosis", IcdQuery);
        if (string.IsNullOrWhiteSpace(text)) return;
        var (ok, reason) = await ReasonIfLockedAsync();
        if (!ok) return;
        if (await RunAsync(() => sync.Encounters.AddDiagnosisAsync(_encounterId, null, text, Diagnoses.Count == 0, reason)))
            await RefreshListsAsync();
    }

    [RelayCommand]
    private async Task RemoveDiagnosisAsync(DiagnosisRow row)
    {
        var (ok, reason) = await ReasonIfLockedAsync();
        if (!ok) return;
        if (await RunAsync(() => sync.Encounters.RemoveDiagnosisAsync(row.Diagnosis.Id, reason))) await RefreshListsAsync();
    }

    // ---- prescription

    private async Task SearchDrugsAsync()
    {
        DrugResults.Clear();
        if (string.IsNullOrWhiteSpace(DrugQuery) || DrugQuery.Length < 2) return;
        foreach (var d in await sync.Prescriptions.SearchDrugsAsync(DrugQuery, 12)) DrugResults.Add(d);
    }

    [RelayCommand]
    private async Task AddDrugAsync(LocalDrug drug)
    {
        var (ok, reason) = await ReasonIfLockedAsync();
        if (!ok) return;
        if (await RunAsync(() => sync.Prescriptions.AddDrugAsync(_encounterId, drug, reason)))
        {
            DrugQuery = null;
            await RefreshListsAsync();
            if (Warnings.FirstOrDefault(w => w.StartsWith("⛔")) is { } critical) await Ui.AlertAsync("Check prescription", critical);
        }
    }

    [RelayCommand]
    private async Task EditItemAsync(RxRow row)
    {
        var item = row.Item;
        var choice = await Shell.Current.DisplayActionSheetAsync(row.Title, "Close", "Remove", "Dose", "Frequency", "Duration", "Instructions");
        if (choice is null or "Close") return;
        if (choice == "Remove")
        {
            var (okR, reasonR) = await ReasonIfLockedAsync();
            if (okR && await RunAsync(() => sync.Prescriptions.RemoveItemAsync(item.Id, reasonR))) await RefreshListsAsync();
            return;
        }

        switch (choice)
        {
            case "Dose":
                item.Dose = await Ui.PromptAsync("Dose", "e.g. 1 tablet, 5 ml", item.Dose) ?? item.Dose;
                break;
            case "Frequency":
                var f = await Shell.Current.DisplayActionSheetAsync("Frequency", "Cancel", null, Frequencies.All.Select(x => $"{x.English} · {x.Urdu}").ToArray());
                item.Frequency = Frequencies.All.FirstOrDefault(x => f?.StartsWith(x.English) == true)?.Code ?? item.Frequency;
                break;
            case "Duration":
                item.DurationDays = int.TryParse(await Ui.PromptAsync("Duration", "Days", item.DurationDays?.ToString()), out var days) ? days : item.DurationDays;
                break;
            case "Instructions":
                item.Instructions = await Ui.PromptAsync("Instructions", "e.g. after meals", item.Instructions) ?? item.Instructions;
                break;
        }
        var (ok, reason) = await ReasonIfLockedAsync();
        if (ok && await RunAsync(() => sync.Prescriptions.UpdateItemAsync(item, reason))) await RefreshListsAsync();
    }

    [RelayCommand]
    private async Task ApplyFavouriteAsync()
    {
        if (_encounter is null) return;
        var templates = await sync.Prescriptions.TemplatesAsync(_encounter.DoctorId);
        if (templates.Count == 0) { await Ui.AlertAsync("Favourites", "No favourites yet. Build a prescription and save it as a favourite."); return; }
        var pick = await Shell.Current.DisplayActionSheetAsync("Apply favourite", "Cancel", null, templates.Select(t => t.Name).ToArray());
        if (templates.FirstOrDefault(t => t.Name == pick) is not { } template) return;
        var (ok, reason) = await ReasonIfLockedAsync();
        if (ok && await RunAsync(() => sync.Prescriptions.ApplyTemplateAsync(_encounterId, template.Id, reason))) await RefreshListsAsync();
    }

    [RelayCommand]
    private async Task SaveFavouriteAsync()
    {
        var name = await Ui.PromptAsync("Save favourite", "Name, e.g. Viral fever adult");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await RunAsync(() => sync.Prescriptions.SaveAsTemplateAsync(_encounterId, name)))
            await Ui.AlertAsync("Favourites", $"Saved \"{name}\".");
    }

    // ---- sign and print

    [RelayCommand]
    private async Task SignAsync()
    {
        if (NotesModel is { } m && !m.Validate()) { ErrorMessage = "Complete the required fields before signing."; return; }
        if (Warnings.Any(w => w.StartsWith("⛔")) &&
            !await Ui.ConfirmAsync("Allergy warning", string.Join("\n", Warnings) + "\n\nSign anyway?", "Sign anyway"))
            return;
        if (!await Ui.ConfirmAsync("Sign", "Sign and lock this encounter? Later changes will need a reason.", "Sign")) return;

        if (await RunAsync(async () =>
            {
                if (NotesModel is { } model) await sync.Encounters.SaveNotesAsync(_encounterId, model.ValuesToSave(), FollowUp);
                await sync.Encounters.SignAsync(_encounterId);
                if (_encounter?.AppointmentId is { } aid &&
                    await sync.Store.GetAsync<LocalAppointment>(aid) is { Status: AppointmentStatus.WithDoctor })
                    await sync.Scheduling.CompleteAsync(aid);
            }))
        {
            await ReloadAsync();
            if (await Ui.ConfirmAsync("Signed", "Print the prescription now?", "Print")) await PrintAsync();
        }
    }

    /// <summary>FR-RX-04 / FR-OFF-25: render the PDF on the device and hand it to the system viewer to print or share.</summary>
    [RelayCommand]
    private async Task PrintAsync()
    {
        await RunAsync(async () =>
        {
            var clinic = session.Me?.TenantName ?? "Clinic";
            var document = await sync.Prescriptions.BuildDocumentAsync(_encounterId, clinic);
            var bytes = PrescriptionPdf.Render(document);
            var path = Path.Combine(FileSystem.CacheDirectory, $"Prescription-{PatientName.Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
            await File.WriteAllBytesAsync(path, bytes);
            await Launcher.Default.OpenAsync(new OpenFileRequest("Prescription", new ReadOnlyFile(path, "application/pdf")));
        });
    }

    [RelayCommand]
    private async Task ShareAsync()
    {
        await RunAsync(async () =>
        {
            var document = await sync.Prescriptions.BuildDocumentAsync(_encounterId, session.Me?.TenantName ?? "Clinic");
            var path = Path.Combine(FileSystem.CacheDirectory, $"Prescription-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
            await File.WriteAllBytesAsync(path, PrescriptionPdf.Render(document));
            await Share.Default.RequestAsync(new ShareFileRequest("Prescription", new ShareFile(path, "application/pdf")));
        });
    }

    // ------------------------------------------------------------------ Phase 5b: case, referral, procedures, on-device assist

    private async Task RefreshCareAsync()
    {
        if (_encounter is not { } e) return;
        CaseText = e.CaseId is { } cid && await sync.Cases.GetAsync(cid) is { } c ? $"Case: {c.Title} ({CaseStatus.Display(c.Status)})" : "Not part of a case";
        LabTests.Clear();
        foreach (var o in await sync.Labs.ForEncounterAsync(e.Id))
        {
            var results = await sync.Labs.ResultsAsync(o.Id);
            LabTests.Add(new CareRow(o.Id, string.Join(", ", results.Select(r => r.TestName)),
                string.Join(" · ", results.Select(r => $"{r.TestCode}: {LabStatus.Display(r.Status)}{(r.IsAbnormal ? " ▲" : "")}")), results.Any(r => r.IsAbnormal)));
        }
        Procedures.Clear();
        foreach (var p in await sync.Procedures.ForEncounterAsync(e.Id))
            Procedures.Add(new CareRow(p.Id, p.Name, ProcedureStatus.Display(p.Status) + (p.ConsentRequired && p.ConsentSignedAt is null ? " · consent needed" : ""),
                p.ConsentRequired && p.ConsentSignedAt is null));
    }

    /// <summary>FR-CASE-03: file this visit under one of the patient's open cases (or open a new one).</summary>
    [RelayCommand]
    private async Task LinkCaseAsync()
    {
        if (_encounter is not { } e) return;
        var open = await sync.Cases.ForPatientAsync(e.PatientId, openOnly: true);
        var options = open.Select(c => c.Title).Append("Open a new case…").ToList();
        if (e.CaseId is not null) options.Add("Remove from case");
        var pick = await CareUi.PickAsync("Case for this visit", options);
        if (pick is null) return;
        if (pick == "Open a new case…") { await Ui.GoToAsync(Routes.Case, new Dictionary<string, object> { ["patientId"] = e.PatientId }); return; }
        var target = pick == "Remove from case" ? null : open.FirstOrDefault(c => c.Title == pick)?.Id;
        await RunAsync(async () =>
        {
            await sync.Cases.LinkEncounterAsync(e.Id, target);
            _encounter = await sync.Encounters.GetAsync(e.Id);
            await RefreshCareAsync();
        });
    }

    [RelayCommand]
    private Task OpenCaseAsync() => _encounter?.CaseId is { } cid
        ? Ui.GoToAsync(Routes.Case, new Dictionary<string, object> { ["caseId"] = cid })
        : LinkCaseAsync();

    [RelayCommand]
    private async Task ReferAsync()
    {
        if (_encounter is not { } e) return;
        await AutosaveAsync();
        var query = new Dictionary<string, object> { ["patientId"] = e.PatientId, ["encounterId"] = e.Id };
        if (e.CaseId is { } cid) query["caseId"] = cid;
        await Ui.GoToAsync(Routes.Referral, query);
    }

    [RelayCommand]
    private Task NewProcedureAsync()
    {
        if (_encounter is not { } e) return Task.CompletedTask;
        var query = new Dictionary<string, object> { ["patientId"] = e.PatientId, ["encounterId"] = e.Id };
        if (e.CaseId is { } cid) query["caseId"] = cid;
        return Ui.GoToAsync(Routes.Procedure, query);
    }

    [RelayCommand]
    private Task OpenProcedureAsync(CareRow row) => Ui.GoToAsync(Routes.Procedure, new Dictionary<string, object> { ["procedureId"] = row.Id });

    /// <summary>FR-AI-22: read the prescription to the patient in English or Urdu (offline platform voices).</summary>
    [RelayCommand]
    private async Task ReadAloudAsync(string language)
    {
        var lines = (await sync.Prescriptions.ItemsAsync(_encounterId))
            .Select(i => new PatientInstructions.Line($"{i.DrugName} {i.Strength}".Trim(), i.Dose, i.Frequency, i.DurationDays, i.Instructions));
        var text = PatientInstructions.Build(lines, language, FollowUp);
        if (text.Count == 0) { await Ui.AlertAsync("Read aloud", "There is nothing to read yet."); return; }
        await voice.SpeakAsync(text, language);
    }

    [RelayCommand]
    private void StopReading() => voice.Stop();

    /// <summary>FR-AI-21: photograph an outside report and propose its numbers for the vitals form.</summary>
    [RelayCommand]
    private async Task ScanVitalsAsync()
    {
        if (VitalsModel is not { } m) return;
        var applied = await OcrReview.ScanIntoAsync(scanner, m);
        if (applied > 0) await Ui.AlertAsync("Scan", $"{applied} value(s) filled in. Check them, then save the vitals.");
    }
}
