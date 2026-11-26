using System.Text.Json.Nodes;
using Clinic.App.Services;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

/// <summary>
/// Edits a handover note: fixed core fields plus the tenant's dynamic "details" form (§5). Everything works
/// offline; a draft is autosaved every 10 s and when leaving the page (FR-DYN-23).
/// </summary>
public sealed partial class NoteEditViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private static readonly TimeSpan AutosaveEvery = TimeSpan.FromSeconds(10);

    private Guid? _noteId;
    private Guid _branchId;
    private FormVersionInfo? _version;
    private bool _coreDirty;
    private bool _saved;
    private bool _loading;
    private IDispatcherTimer? _autosave;

    public IReadOnlyList<string> ColorNames => NoteColors.Names;

    [ObservableProperty] public partial string PageTitle { get; set; } = "New note";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string? Body { get; set; }
    [ObservableProperty] public partial bool IsPinned { get; set; }
    [ObservableProperty] public partial string Color { get; set; } = "none";
    [ObservableProperty] public partial bool IsExisting { get; set; }
    [ObservableProperty] public partial string? SyncInfo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForm))]
    public partial DynamicFormModel? FormModel { get; set; }

    [ObservableProperty] public partial string? FormInfo { get; set; }
    [ObservableProperty] public partial string? DraftInfo { get; set; }
    [ObservableProperty] public partial string LanguageButtonText { get; set; } = "اردو";

    public bool HasForm => FormModel is not null;

    partial void OnTitleChanged(string value) => MarkDirty();
    partial void OnBodyChanged(string? value) => MarkDirty();
    partial void OnIsPinnedChanged(bool value) => MarkDirty();
    partial void OnColorChanged(string value) => MarkDirty();

    private void MarkDirty() { if (!_loading) _coreDirty = true; }

    private bool IsDirty => _coreDirty || FormModel?.IsDirty == true;

    private string DraftKey => _noteId is { } id
        ? DynamicDataService.DraftKey(SyncEntityNames.BranchNote, id)
        : $"{SyncEntityNames.BranchNote}:new:{_branchId}";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("branchId", out var b) && b is Guid branchId) _branchId = branchId;
        _noteId = query.TryGetValue("noteId", out var n) && n is Guid id ? id : null;
        IsExisting = _noteId is not null;
        PageTitle = IsExisting ? "Edit note" : "New note";
        _ = LoadAsync();
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        _loading = true;
        try
        {
            LocalBranchNote? note = null;
            if (_noteId is { } id)
            {
                note = await sync.Store.GetAsync<LocalBranchNote>(id);
                if (note is null)
                {
                    ErrorMessage = "This note was deleted.";
                    return;
                }
                _branchId = note.BranchId;
                Title = note.Title;
                Body = note.Body;
                IsPinned = note.IsPinned;
                Color = note.Color ?? "none";
                SyncInfo = note.SyncStatus == SyncStatus.Synced
                    ? $"Synced {note.LastSyncedAt?.ToLocalTime():g}"
                    : "Changes are saved on this device and will sync automatically.";
            }

            // FR-DYN-11/24: an existing note renders with the version it was captured with; new notes use the current one.
            var forms = sync.Forms;
            _version = note?.FormVersionId is { } vid
                ? await forms.GetVersionAsync(vid)
                : await forms.ResolveFormAsync(SyncEntityNames.BranchNote, _branchId);

            JsonObject values = note is null ? [] : await forms.LoadValuesAsync(SyncEntityNames.BranchNote, note.Id);

            // FR-DYN-23: restore an autosaved draft (e.g. after a crash).
            if (await forms.LoadDraftAsync(DraftKey) is { } draft)
            {
                if (draft["core"] is JsonObject core)
                {
                    Title = FormValues.AsString(core["title"]) ?? Title;
                    Body = FormValues.AsString(core["body"]) ?? Body;
                    IsPinned = FormValues.AsBool(core["isPinned"]) ?? IsPinned;
                    Color = FormValues.AsString(core["color"]) ?? Color;
                }
                if (draft["dynamic"] is JsonObject saved) values = saved;
                DraftInfo = "Restored changes that were not saved last time.";
                _coreDirty = true;
            }

            if (_version is null)
            {
                FormModel = null;
                FormInfo = null;
            }
            else
            {
                var lookups = await forms.LoadLookupsAsync(_version.Definition);
                FormModel = new DynamicFormModel(_version, values, new EvaluationContext
                {
                    Roles = session.Me?.Roles ?? [],
                    Language = FormModel?.Language ?? "en",
                }, lookups, isNewRecord: note is null);

                FormInfo = !_version.IsCurrent && note is not null
                    ? $"Recorded with {_version.Definition.Name.En} v{_version.VersionNo}. A newer version exists; this note keeps the version it was written with."
                    : $"{_version.Definition.Name.En} · v{_version.VersionNo}";
            }
        }
        finally
        {
            _loading = false;
        }
        StartAutosave();
    });

    private void StartAutosave()
    {
        if (_autosave is not null || Application.Current?.Dispatcher is not { } dispatcher) return;
        _autosave = dispatcher.CreateTimer();
        _autosave.Interval = AutosaveEvery;
        _autosave.Tick += async (_, _) => await SaveDraftAsync();
        _autosave.Start();
    }

    private async Task SaveDraftAsync()
    {
        if (_saved || !IsDirty || !sync.IsStarted) return;
        var draft = new JsonObject
        {
            ["core"] = new JsonObject { ["title"] = Title, ["body"] = Body, ["isPinned"] = IsPinned, ["color"] = Color },
            ["dynamic"] = FormModel?.Values.DeepClone(),
        };
        await sync.Forms.SaveDraftAsync(DraftKey, draft);
    }

    /// <summary>Called by the page when it disappears (navigation, app backgrounded).</summary>
    public async Task OnLeavingAsync()
    {
        _autosave?.Stop();
        _autosave = null;
        await SaveDraftAsync();
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        if (FormModel is null) return;
        FormModel.Language = FormModel.Language == "ur" ? "en" : "ur";
        LanguageButtonText = FormModel.Language == "ur" ? "English" : "اردو";
        // Rebind so the view rebuilds labels and right-to-left layout.
        var model = FormModel;
        FormModel = null;
        FormModel = model;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Title))
        {
            ErrorMessage = "Give the note a title.";
            return;
        }
        if (FormModel is { } model && !model.Validate())
        {
            ErrorMessage = $"Please fix {model.Evaluation.Errors.Count()} highlighted field(s).";
            return;
        }

        var ok = await RunAsync(async () =>
        {
            var note = _noteId is { } id
                ? await sync.Store.GetAsync<LocalBranchNote>(id) ?? throw new InvalidOperationException("This note was deleted.")
                : new LocalBranchNote { Id = Guid.NewGuid(), BranchId = _branchId };

            if (FormModel is { } m) await CheckUniqueAsync(m, note.Id);

            note.Title = Title.Trim();
            note.Body = string.IsNullOrWhiteSpace(Body) ? null : Body.Trim();
            note.IsPinned = IsPinned;
            note.Color = Color == "none" ? null : Color;

            if (FormModel is { } form && _version is not null)
            {
                await DynamicDataService.SaveRecordAsync(sync.Store, note, _noteId is null, _version, form.ValuesToSave(),
                    SyncEntityNames.BranchNote, note.BranchId);
            }
            else if (_noteId is null)
            {
                await sync.Store.InsertAsync(note);
            }
            else
            {
                await sync.Store.UpdateAsync(note);
            }

            _saved = true;
            await sync.Forms.DeleteDraftAsync(DraftKey);
            if (_noteId is null)
                await sync.Forms.DeleteDraftAsync(DynamicDataService.DraftKey(SyncEntityNames.BranchNote, note.Id));
        });

        if (ok)
        {
            _autosave?.Stop();
            await Ui.GoBackAsync();
        }
    }

    /// <summary>FR-DYN-02 "unique within tenant", checked offline against everything this device holds.</summary>
    private async Task CheckUniqueAsync(DynamicFormModel model, Guid recordId)
    {
        foreach (var field in model.Definition.AllFields.Where(f => f.Unique))
        {
            if (FormValues.AsString(model.GetValue(field.Key)) is not { Length: > 0 } value) continue;
            if (await sync.Forms.IsTakenAsync(SyncEntityNames.BranchNote, field.Key, value, recordId))
                throw new InvalidOperationException($"{field.Label.En} '{value}' is already used by another record.");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_noteId is not { } id) return;
        if (!await Ui.ConfirmAsync("Delete note", "Delete this note on all devices?", "Delete")) return;
        if (await RunAsync(() => sync.Store.DeleteAsync<LocalBranchNote>(id)))
        {
            _saved = true;
            await sync.Forms.DeleteDraftAsync(DraftKey);
            await Ui.GoBackAsync();
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (IsDirty && !await Ui.ConfirmAsync("Discard changes", "Discard the changes you made?", "Discard")) return;
        _saved = true;
        _autosave?.Stop();
        if (sync.IsStarted) await sync.Forms.DeleteDraftAsync(DraftKey);
        await Ui.GoBackAsync();
    }
}
