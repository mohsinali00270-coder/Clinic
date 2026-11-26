using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Clinic.App.Services;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record PatientRow(LocalPatient Patient)
{
    public string Title => Patient.FullName;
    public string Subtitle
    {
        get
        {
            var age = PatientRules.AgeYears(Patient.DateOfBirth, DateTime.Today) is { } a ? $"{a}{(Patient.DobEstimated ? "~" : "")} y" : "age ?";
            return $"{Patient.Mrn} · {Patient.Gender} · {age}" + (Patient.Phone is { } p ? $" · {p}" : "");
        }
    }
    public bool Pending => Patient.SyncStatus != SyncStatus.Synced;
}

/// <summary>FR-PAT-03: find patients offline by name, phone, MRN or ID number.</summary>
public sealed partial class PatientsViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase
{
    public ObservableCollection<PatientRow> Patients { get; } = [];

    [ObservableProperty] public partial string? Query { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public bool CanRegister => session.Has(Permissions.Patients.Create);

    partial void OnQueryChanged(string? value) => SearchCommand.Execute(null);

    [RelayCommand]
    private Task SearchAsync() => RunAsync(async () =>
    {
        if (!sync.IsStarted) return;
        var results = await sync.Patients.SearchAsync(Query ?? "");
        Patients.Clear();
        foreach (var p in results) Patients.Add(new PatientRow(p));
        Summary = string.IsNullOrWhiteSpace(Query) ? $"{results.Count} recent patient(s)" : $"{results.Count} match(es)";
        OnPropertyChanged(nameof(CanRegister));
    });

    [RelayCommand]
    private Task RegisterAsync() => Ui.GoToAsync(Routes.PatientEdit);

    [RelayCommand]
    private Task OpenAsync(PatientRow row) =>
        Ui.GoToAsync(Routes.Patient, new Dictionary<string, object> { ["patientId"] = row.Patient.Id });
}

/// <summary>FR-PAT-01/04: register or edit — core fields, duplicate warning, and the Patient dynamic form.</summary>
public sealed partial class PatientEditViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid? _patientId;
    private FormVersionInfo? _version;
    private Dictionary<string, List<LocalLookupItem>>? _lookups;

    public IReadOnlyList<string> Genders { get; } = ["F", "M", "O"];
    public ObservableCollection<PatientRow> Duplicates { get; } = [];

    [ObservableProperty] public partial string PageTitle { get; set; } = "Register patient";
    [ObservableProperty] public partial string FullName { get; set; } = "";
    [ObservableProperty] public partial string? Gender { get; set; }
    [ObservableProperty] public partial bool UseAge { get; set; }
    [ObservableProperty] public partial DateTime? DateOfBirth { get; set; }
    [ObservableProperty] public partial string? Age { get; set; }
    [ObservableProperty] public partial string? Phone { get; set; }
    [ObservableProperty] public partial string? Mrn { get; set; }
    [ObservableProperty] public partial bool HasDuplicates { get; set; }
    [ObservableProperty] public partial DynamicFormModel? FormModel { get; set; }

    partial void OnFullNameChanged(string value) => _ = CheckDuplicatesAsync();
    partial void OnPhoneChanged(string? value) => _ = CheckDuplicatesAsync();
    partial void OnGenderChanged(string? value) => RebuildVariables();
    partial void OnDateOfBirthChanged(DateTime? value) => RebuildVariables();
    partial void OnAgeChanged(string? value) => RebuildVariables();

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _patientId = query.TryGetValue("patientId", out var v) && v is Guid id ? id : null;
        PageTitle = _patientId is null ? "Register patient" : "Edit patient";
        _ = LoadAsync();
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        JsonObject values = [];
        LocalPatient? p = null;
        if (_patientId is { } id)
        {
            p = await sync.Store.GetAsync<LocalPatient>(id) ?? throw new InvalidOperationException("Patient not found.");
            FullName = p.FullName;
            Gender = p.Gender;
            DateOfBirth = p.DateOfBirth;
            UseAge = p.DobEstimated;
            Age = PatientRules.AgeYears(p.DateOfBirth, DateTime.Today)?.ToString();
            Phone = p.Phone;
            Mrn = p.Mrn;
            values = await sync.Forms.LoadValuesAsync(SyncEntityNames.Patient, id);
        }

        _version = p?.FormVersionId is { } vid
            ? await sync.Forms.GetVersionAsync(vid)
            : await sync.Forms.ResolveFormAsync(SyncEntityNames.Patient, sync.CurrentBranchId);
        if (_version is not null)
        {
            _lookups = await sync.Forms.LoadLookupsAsync(_version.Definition);
            FormModel = new DynamicFormModel(_version, values, Context(), _lookups, isNewRecord: p is null);
        }
    });

    private DateTime? EffectiveDob => UseAge
        ? int.TryParse(Age, out var years) && years is >= 0 and <= 130 ? PatientService.DobFromAge(years, DateTime.Today) : null
        : DateOfBirth;

    private EvaluationContext Context() => new()
    {
        Roles = session.Me?.Roles ?? [],
        Variables = PatientRules.FormVariables(Gender, EffectiveDob, DateTime.Today),
        Language = FormModel?.Language ?? "en",
    };

    /// <summary>Gender and age drive conditions such as "show LMP for women 12+": rebuild the form context.</summary>
    private void RebuildVariables()
    {
        if (FormModel is not { } m || _version is null) return;
        FormModel = new DynamicFormModel(_version, m.Values, Context(), _lookups, isNewRecord: _patientId is null);
    }

    private async Task CheckDuplicatesAsync()
    {
        if (!sync.IsStarted || string.IsNullOrWhiteSpace(FullName)) return;
        var found = await sync.Patients.FindDuplicatesAsync(FullName, Phone, EffectiveDob, _patientId);
        Duplicates.Clear();
        foreach (var p in found) Duplicates.Add(new PatientRow(p));
        HasDuplicates = Duplicates.Count > 0;
    }

    [RelayCommand]
    private Task OpenDuplicateAsync(PatientRow row) =>
        Ui.GoToAsync(Routes.Patient, new Dictionary<string, object> { ["patientId"] = row.Patient.Id });

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        if (Gender is null) { ErrorMessage = "Choose a gender."; return; }
        if (UseAge && EffectiveDob is null) { ErrorMessage = "Enter the age in years."; return; }
        if (FormModel is { } m && !m.Validate()) { ErrorMessage = $"Please fix {m.Evaluation.Errors.Count()} highlighted field(s)."; return; }

        if (_patientId is null && HasDuplicates &&
            !await Ui.ConfirmAsync("Possible duplicate", $"{Duplicates.Count} existing patient(s) look like this person. Register a new record anyway?", "Register"))
            return;

        LocalPatient? saved = null;
        var ok = await RunAsync(async () =>
        {
            var input = new PatientInput(FullName, Gender, EffectiveDob, UseAge, Phone, sync.CurrentBranchId);
            if (FormModel is { } model)
            {
                foreach (var f in model.Definition.AllFields.Where(f => f.Unique))
                {
                    if (FormValues.AsString(model.GetValue(f.Key)) is { Length: > 0 } v &&
                        await sync.Forms.IsTakenAsync(SyncEntityNames.Patient, f.Key, v, _patientId ?? Guid.Empty))
                        throw new InvalidOperationException($"{f.Label.En} {v} belongs to another patient.");
                }
            }
            var values = FormModel?.ValuesToSave();
            saved = _patientId is { } id
                ? await sync.Patients.UpdateAsync(id, input, _version, values)
                : await sync.Patients.RegisterAsync(input, _version, values);
        });
        if (!ok) return;

        if (_patientId is null)
            await Shell.Current.GoToAsync($"../{Routes.Patient}", new Dictionary<string, object> { ["patientId"] = saved!.Id });
        else
            await Ui.GoBackAsync();
    }

    [RelayCommand]
    private Task CancelAsync() => Ui.GoBackAsync();
}

public sealed record TimelineRow(TimelineItem Item)
{
    public string When => Item.At.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    public string Icon => Item.Kind switch { "appointment" => "📅", "file" => "📎", "encounter" => "🩺", _ => "🆕" };
}

/// <summary>FR-PAT-05/06: patient summary, timeline, files, and quick booking / walk-in.</summary>
public sealed partial class PatientViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable, IDisposable
{
    private Guid _patientId;
    private bool _subscribed;

    public ObservableCollection<TimelineRow> Timeline { get; } = [];
    public ObservableCollection<LocalAttachment> Files { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string? DynamicSummary { get; set; }
    [ObservableProperty] public partial string? SyncText { get; set; }

    public bool CanEdit => session.Has(Permissions.Patients.Edit);
    public bool CanBook => session.Has(Permissions.Appointments.Book);
    public bool CanUpload => session.Has(Permissions.Attachments.Upload);
    public bool CanConsult => session.Has(Permissions.Encounters.Edit);

    /// <summary>Starts a consultation with the signed-in doctor (or a chosen one).</summary>
    [RelayCommand]
    private async Task NewConsultationAsync()
    {
        var doctors = await sync.Scheduling.DoctorsAtBranchAsync(sync.CurrentBranchId);
        var doctor = doctors.FirstOrDefault(d => d.UserId == session.Me?.UserId);
        if (doctor is null)
        {
            var pick = await Shell.Current.DisplayActionSheetAsync("Consultation with", "Cancel", null, doctors.Select(d => d.FullName).ToArray());
            doctor = doctors.FirstOrDefault(d => d.FullName == pick);
        }
        if (doctor is null) return;
        LocalEncounter? e = null;
        if (await RunAsync(async () => e = await sync.Encounters.StartAsync(_patientId, doctor.Id, sync.CurrentBranchId)))
            await Ui.GoToAsync(Routes.Encounter, new Dictionary<string, object> { ["encounterId"] = e!.Id });
    }

    [RelayCommand]
    private Task OpenTimelineAsync(TimelineRow row) => row.Item.Kind == "encounter" && row.Item.RecordId is { } id
        ? Ui.GoToAsync(Routes.Encounter, new Dictionary<string, object> { ["encounterId"] = id })
        : Task.CompletedTask;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("patientId", out var v) && v is Guid id) _patientId = id;
        if (!_subscribed) { sync.DataChanged += OnDataChanged; _subscribed = true; }
        LoadCommand.Execute(null);
    }

    private void OnDataChanged(object? sender, IReadOnlyCollection<string> types) => LoadCommand.Execute(null);

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var p = await sync.Store.GetAsync<LocalPatient>(_patientId) ?? throw new InvalidOperationException("Patient not found.");
        Name = p.FullName;
        Details = new PatientRow(p).Subtitle;
        SyncText = p.SyncStatus == SyncStatus.Synced ? null : "Saved on this device · waiting to sync";

        DynamicSummary = null;
        if (p.FormVersionId is { } vid && await sync.Forms.GetVersionAsync(vid) is { } version && !string.IsNullOrEmpty(p.DynamicData)
            && JsonNode.Parse(p.DynamicData) is JsonObject values)
        {
            var lines = version.Definition.AllFields
                .Where(f => f.HoldsValue && !f.Sensitive && !FormValues.IsEmpty(values[f.Key]))
                .Take(8)
                .Select(f => $"{f.Label.En}: {Display(values[f.Key])}");
            DynamicSummary = string.Join("\n", lines);
        }

        Timeline.Clear();
        foreach (var t in await sync.Patients.TimelineAsync(_patientId)) Timeline.Add(new TimelineRow(t));
        Files.Clear();
        foreach (var f in await sync.Store.ListAsync<LocalAttachment>(a => a.RecordId == _patientId)) Files.Add(f);
        OnPropertyChanged(nameof(CanEdit));
    });

    private static string Display(JsonNode? n) => n is JsonArray a ? string.Join(", ", a.Select(FormValues.AsString)) : FormValues.AsString(n) ?? "";

    [RelayCommand]
    private Task EditAsync() => Ui.GoToAsync(Routes.PatientEdit, new Dictionary<string, object> { ["patientId"] = _patientId });

    [RelayCommand]
    private Task BookAsync() => Ui.GoToAsync(Routes.Booking, new Dictionary<string, object> { ["patientId"] = _patientId });

    /// <summary>FR-APT-02/21: put the patient straight into a doctor's queue with a token.</summary>
    [RelayCommand]
    private async Task WalkInAsync()
    {
        var doctors = await sync.Scheduling.DoctorsAtBranchAsync(sync.CurrentBranchId);
        if (doctors.Count == 0) { await Ui.AlertAsync("Walk-in", "No doctor has a schedule at this branch yet."); return; }
        var pick = await Shell.Current.DisplayActionSheetAsync("Walk-in to which doctor?", "Cancel", null, doctors.Select(d => d.FullName).ToArray());
        var doctor = doctors.FirstOrDefault(d => d.FullName == pick);
        if (doctor is null) return;
        var priority = await Shell.Current.DisplayActionSheetAsync("Priority", "Cancel", null, "Normal", "Priority (elderly / pregnant)", "Emergency");
        if (priority is null or "Cancel") return;
        var code = priority.StartsWith("Emergency") ? QueuePriority.Emergency : priority.StartsWith("Priority") ? QueuePriority.Priority : QueuePriority.Normal;

        LocalAppointment? walkIn = null;
        if (await RunAsync(async () => walkIn = await sync.Scheduling.WalkInAsync(_patientId, doctor.Id, sync.CurrentBranchId, code)))
            await Ui.AlertAsync("Walk-in", $"Token {walkIn!.TokenNo} for {doctor.FullName}.");
        await LoadAsync();
    }

    [RelayCommand]
    private Task AddPhotoAsync() => AddFileAsync(photo: true);

    [RelayCommand]
    private Task AddDocumentAsync() => AddFileAsync(photo: false);

    private async Task AddFileAsync(bool photo)
    {
        FileResult? file;
        try
        {
            file = photo && MediaPicker.Default.IsCaptureSupported
                ? await MediaPicker.Default.CapturePhotoAsync()
                : await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Photo or PDF" });
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
            await Ui.AlertAsync("Camera", ex.Message);
            return;
        }
        if (file is null) return;

        var caption = await Ui.PromptAsync("Attachment", "Caption (optional)", Path.GetFileNameWithoutExtension(file.FileName));
        var patient = await sync.Store.GetAsync<LocalPatient>(_patientId);
        await RunAsync(async () =>
        {
            await using var stream = await file.OpenReadAsync();
            var type = file.ContentType ?? (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "image/jpeg");
            await sync.Attachments.AddAsync(SyncEntityNames.Patient, _patientId, patient?.BranchId, stream, file.FileName, type,
                string.IsNullOrWhiteSpace(caption) ? null : caption, photo ? "photo" : type == "application/pdf" ? "document" : "photo");
        });
        await LoadAsync();
    }

    /// <summary>Decrypts (downloading if needed) to a temporary file and opens it with the system viewer.</summary>
    [RelayCommand]
    private async Task OpenFileAsync(LocalAttachment file)
    {
        await RunAsync(async () =>
        {
            var bytes = await sync.Attachments.ReadAsync(file, sync.Files);
            var path = Path.Combine(FileSystem.CacheDirectory, file.FileName);
            await File.WriteAllBytesAsync(path, bytes);
            await Launcher.Default.OpenAsync(new OpenFileRequest(file.Caption ?? file.FileName, new ReadOnlyFile(path, file.ContentType)));
        });
    }

    public void Dispose()
    {
        if (_subscribed) sync.DataChanged -= OnDataChanged;
    }
}
