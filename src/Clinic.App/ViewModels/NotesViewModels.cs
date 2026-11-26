using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Client.Core.Local;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record NoteRow(LocalBranchNote Note)
{
    public string Title => Note.IsPinned ? $"📌 {Note.Title}" : Note.Title;
    public string? Body => Note.Body;
    public string When => $"{(Note.UpdatedAt ?? Note.CreatedAt).ToLocalTime():g}";
    public string? SyncText => Note.SyncStatus switch
    {
        SyncStatus.Pending => "Waiting to sync",
        SyncStatus.Conflict => "Conflict – open Sync Center",
        SyncStatus.Error => "Not accepted – open Sync Center",
        _ => null,
    };
    public bool HasSyncText => SyncText is not null;
    public Color Accent => NoteColors.Resolve(Note.Color);
}

public static class NoteColors
{
    public static readonly IReadOnlyList<string> Names = ["none", "amber", "red", "green", "blue"];

    public static Color Resolve(string? name) => name switch
    {
        "amber" => Color.FromArgb("#F59E0B"),
        "red" => Color.FromArgb("#DC2626"),
        "green" => Color.FromArgb("#16A34A"),
        "blue" => Color.FromArgb("#2563EB"),
        _ => Colors.Transparent,
    };
}

/// <summary>
/// Branch handover notes. Reads and writes only the local database; works the same online and offline.
/// </summary>
public sealed partial class NotesViewModel : ViewModelBase, IDisposable
{
    private readonly SyncCoordinator _sync;
    private readonly SessionStore _session;

    public NotesViewModel(SyncCoordinator sync, SessionStore session)
    {
        _sync = sync;
        _session = session;
        _sync.DataChanged += OnDataChanged;
    }

    public SyncCoordinator Sync => _sync;
    public ObservableCollection<NoteRow> Notes { get; } = [];
    public ObservableCollection<LocalBranch> Branches { get; } = [];

    [ObservableProperty] public partial LocalBranch? SelectedBranch { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    public bool CanManage => _session.Has(Permissions.Notes.Manage);

    partial void OnSelectedBranchChanged(LocalBranch? value)
    {
        if (value is not null) Preferences.Default.Set("notes_branch", value.Id.ToString());
        _ = LoadNotesAsync();
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        if (!_sync.IsStarted) return;

        // Branches come from the synced config table; only the user's own branches are offered.
        var mine = _session.Me?.Branches.Select(b => b.Id).ToHashSet() ?? [];
        var branches = (await _sync.Store.ListAsync<LocalBranch>(b => b.IsActive))
            .Where(b => mine.Contains(b.Id)).OrderBy(b => b.Code).ToList();

        // Before the first sync the branch table is empty: fall back to the profile's branches.
        if (branches.Count == 0 && _session.Me is { } me)
            branches = me.Branches.Select(b => new LocalBranch { Id = b.Id, Code = b.Code, Name = b.Name }).ToList();

        Branches.Clear();
        foreach (var b in branches) Branches.Add(b);

        var saved = Guid.TryParse(Preferences.Default.Get("notes_branch", ""), out var id) ? id : (Guid?)null;
        var pick = branches.FirstOrDefault(b => b.Id == saved) ?? branches.FirstOrDefault();
        if (pick?.Id != SelectedBranch?.Id) SelectedBranch = pick;
        else await LoadNotesAsync();
        OnPropertyChanged(nameof(CanManage));
    });

    private async Task LoadNotesAsync()
    {
        if (!_sync.IsStarted || SelectedBranch is not { } branch) return;
        var branchId = branch.Id;
        var notes = await _sync.Store.ListAsync<LocalBranchNote>(n => n.BranchId == branchId);

        Notes.Clear();
        foreach (var n in notes.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAt ?? n.CreatedAt))
            Notes.Add(new NoteRow(n));
        IsEmpty = Notes.Count == 0;
    }

    [RelayCommand]
    private Task AddAsync() => SelectedBranch is { } b && CanManage
        ? Ui.GoToAsync(Routes.NoteEdit, new Dictionary<string, object> { ["branchId"] = b.Id })
        : Task.CompletedTask;

    [RelayCommand]
    private Task EditAsync(NoteRow row) => CanManage
        ? Ui.GoToAsync(Routes.NoteEdit, new Dictionary<string, object> { ["noteId"] = row.Note.Id })
        : Task.CompletedTask;

    [RelayCommand]
    private async Task TogglePinAsync(NoteRow row)
    {
        if (!CanManage) return;
        await RunAsync(async () =>
        {
            var note = await _sync.Store.GetAsync<LocalBranchNote>(row.Note.Id);
            if (note is null) return;
            note.IsPinned = !note.IsPinned;
            await _sync.Store.UpdateAsync(note);
        });
    }

    [RelayCommand]
    private void SyncNow() => _sync.RequestSync(manual: true);

    private void OnDataChanged(object? sender, IReadOnlyCollection<string> types)
    {
        if (types.Contains(SyncEntityNames.Branch)) LoadCommand.Execute(null);
        else if (types.Contains(SyncEntityNames.BranchNote)) _ = LoadNotesAsync();
    }

    public void Dispose() => _sync.DataChanged -= OnDataChanged;
}
