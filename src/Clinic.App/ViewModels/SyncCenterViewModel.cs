using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Client.Core.Local;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record OutboxRow(OutboxEntry Entry)
{
    public string Title => Entry.Summary ?? $"{(SyncOperation)Entry.Operation} {Entry.EntityType}";
    public string When => $"Saved {DateTime.SpecifyKind(Entry.ClientTimestamp, DateTimeKind.Utc).ToLocalTime():g}";
    public string StatusText => Entry.Status switch
    {
        OutboxStatus.Pending => "Waiting to sync",
        OutboxStatus.Failed => Entry.NextAttemptAt is { } next
            ? $"Retrying at {DateTime.SpecifyKind(next, DateTimeKind.Utc).ToLocalTime():t} (attempt {Entry.Attempts})"
            : $"Failed (attempt {Entry.Attempts})",
        OutboxStatus.Conflict => "Conflict: someone else changed the same data",
        OutboxStatus.Rejected => "Not accepted by the server",
        _ => "",
    };
    public string? Error => Entry.LastError;
    public bool HasError => !string.IsNullOrEmpty(Entry.LastError);
    public bool CanRetry => Entry.Status is OutboxStatus.Failed or OutboxStatus.Rejected;
    public bool CanKeepMine => Entry.Status == OutboxStatus.Conflict;
    public bool CanDiscard => Entry.Status is OutboxStatus.Conflict or OutboxStatus.Rejected or OutboxStatus.Failed;
    public bool NeedsAttention => Entry.Status is OutboxStatus.Conflict or OutboxStatus.Rejected;
}

/// <summary>FR-OFF-12: pending, failed and conflicted changes with retry / resolve actions.</summary>
public sealed partial class SyncCenterViewModel : ViewModelBase, IDisposable
{
    private readonly SyncCoordinator _sync;

    public SyncCenterViewModel(SyncCoordinator sync)
    {
        _sync = sync;
        _sync.PropertyChanged += OnSyncPropertyChanged;
    }

    public SyncCoordinator Sync => _sync;
    public ObservableCollection<OutboxRow> Entries { get; } = [];
    public bool IsEmpty => Entries.Count == 0;

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        if (!_sync.IsStarted) return;
        var entries = await _sync.Center.ListAsync();
        Entries.Clear();
        foreach (var e in entries.OrderByDescending(e => e.Status is OutboxStatus.Conflict or OutboxStatus.Rejected).ThenBy(e => e.Seq))
            Entries.Add(new OutboxRow(e));
        OnPropertyChanged(nameof(IsEmpty));
    });

    [RelayCommand]
    private void SyncNow() => _sync.RequestSync(manual: true);

    [RelayCommand]
    private async Task RetryAsync(OutboxRow row)
    {
        await RunAsync(() => _sync.Center.RetryAsync(row.Entry.Seq));
        await LoadAsync();
        _sync.RequestSync(manual: true);
    }

    [RelayCommand]
    private async Task KeepMineAsync(OutboxRow row)
    {
        if (!await Ui.ConfirmAsync("Keep my change", "Overwrite the other person's change with yours?", "Keep mine")) return;
        await RunAsync(() => _sync.Center.KeepMineAsync(row.Entry.Seq));
        await LoadAsync();
        _sync.RequestSync(manual: true);
    }

    [RelayCommand]
    private Task CompareAsync(OutboxRow row) =>
        Ui.GoToAsync(Routes.Conflict, new Dictionary<string, object> { ["seq"] = row.Entry.Seq });

    [RelayCommand]
    private async Task DiscardAsync(OutboxRow row)
    {
        var message = row.Entry.ServerData is null
            ? "Discard this change? It will be removed from this device."
            : "Discard your change and use the version from the server?";
        if (!await Ui.ConfirmAsync("Discard change", message, "Discard")) return;
        await RunAsync(() => _sync.Center.DiscardAsync(row.Entry.Seq));
        await LoadAsync();
    }

    private void OnSyncPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Refresh the list whenever a sync run finishes or the queue size changes.
        if (e.PropertyName is nameof(SyncCoordinator.Outstanding) or nameof(SyncCoordinator.IsSyncing) && !_sync.IsSyncing)
            LoadCommand.Execute(null);
    }

    public void Dispose() => _sync.PropertyChanged -= OnSyncPropertyChanged;
}
