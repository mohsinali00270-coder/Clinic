using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Clinic.App.Services;
using Clinic.Client.Core.Sync;
using Clinic.Shared.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

/// <summary>One field of a conflict, with the choice the user made (mine or theirs).</summary>
public sealed partial class ConflictFieldRow(ConflictField field) : ObservableObject
{
    public ConflictField Field { get; } = field;
    public string Name => Humanize(Field.Field);
    public string MineText => Show(Field.Mine);
    public string TheirsText => Show(Field.Theirs);
    public bool Differs => Field.Differs;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UseTheirs))]
    public partial bool UseMine { get; set; } = true;

    public bool UseTheirs
    {
        get => !UseMine;
        set => UseMine = !value;
    }

    private static string Show(JsonNode? v) => v switch
    {
        null => "(empty)",
        JsonValue j when j.TryGetValue<string>(out var s) => string.IsNullOrEmpty(s) ? "(empty)" : s,
        _ => v.ToJsonString(),
    };

    private static string Humanize(string key) =>
        string.Concat(key.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : i == 0 ? char.ToUpperInvariant(c).ToString() : c.ToString()));
}

/// <summary>FR-SYN-30/31: compare a conflicted change field by field, keep mine / theirs / a mix of both.</summary>
public sealed partial class ConflictViewModel(SyncCoordinator sync) : ViewModelBase, IQueryAttributable
{
    private long _seq;

    [ObservableProperty] public partial string Title { get; set; } = "Conflict";
    public ObservableCollection<ConflictFieldRow> Fields { get; } = [];

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = RunAsync(async () =>
    {
        if (query.TryGetValue("seq", out var s) && s is long seq) _seq = seq;
        var entry = (await sync.Center.ListAsync()).FirstOrDefault(e => e.Seq == _seq)
                    ?? throw new InvalidOperationException("This change is no longer waiting.");
        Title = entry.Summary ?? $"{entry.EntityType} change";
        Fields.Clear();
        foreach (var f in await sync.Center.ConflictFieldsAsync(_seq)) Fields.Add(new ConflictFieldRow(f));
    });

    [RelayCommand]
    private void AllMine() { foreach (var f in Fields) f.UseMine = true; }

    [RelayCommand]
    private void AllTheirs() { foreach (var f in Fields) f.UseMine = false; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var accepted = Fields.ToDictionary(f => f.Field.Field, f => (f.UseMine ? f.Field.Mine : f.Field.Theirs)?.DeepClone());
        if (await RunAsync(() => sync.Center.ResolveAsync(_seq, accepted)))
        {
            sync.RequestSync(manual: true);
            await Ui.GoBackAsync();
        }
    }
}

public sealed record AuditRow(AuditEntryDto Entry)
{
    public string Title => $"{Entry.Action} · {Entry.EntityType}";
    public string Who => $"{DateTime.SpecifyKind(Entry.Timestamp, DateTimeKind.Utc).ToLocalTime():g} · {Entry.UserName ?? "system"}";
    public string? Detail => Entry.EntityId;
    public string? Changes => Entry.NewValues is null && Entry.OldValues is null ? null
        : $"{(Entry.OldValues is { } o ? "Before: " + o + "\n" : "")}{(Entry.NewValues is { } n ? "After: " + n : "")}".Trim();
    public bool HasChanges => Changes is not null;
}

/// <summary>FR-SEC-07 audit viewer (online): who changed or opened what, filterable by record, action and date.</summary>
public sealed partial class AuditViewModel(AdminApi api) : ViewModelBase
{
    private int _page = 1;

    [ObservableProperty] public partial string? EntityType { get; set; }
    [ObservableProperty] public partial string? EntityId { get; set; }
    [ObservableProperty] public partial string? Action { get; set; }
    [ObservableProperty] public partial DateTime From { get; set; } = DateTime.Today.AddDays(-7);
    [ObservableProperty] public partial DateTime To { get; set; } = DateTime.Today;
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    public ObservableCollection<AuditRow> Entries { get; } = [];

    [RelayCommand]
    private Task SearchAsync()
    {
        _page = 1;
        Entries.Clear();
        return LoadPageAsync();
    }

    [RelayCommand]
    private Task MoreAsync()
    {
        _page++;
        return LoadPageAsync();
    }

    private Task LoadPageAsync() => RunAsync(async () =>
    {
        var result = await api.GetAuditAsync(EntityType, EntityId, Action, From.Date.ToUniversalTime(), To.Date.AddDays(1).ToUniversalTime(), _page);
        foreach (var e in result.Items) Entries.Add(new AuditRow(e));
        HasMore = Entries.Count < result.TotalCount;
        Summary = $"{result.TotalCount:N0} entries";
    });
}

public sealed record DuplicateRow(DuplicatePairDto Pair)
{
    public string A => Describe(Pair.A);
    public string B => Describe(Pair.B);
    public string Reasons => string.Join(" · ", Pair.Reasons);

    private static string Describe(PatientBrief p) =>
        $"{p.FullName} · {p.Gender} · MRN {p.Mrn}\n{p.Phone ?? "no phone"} · {(p.DateOfBirth is { } d ? d.ToString("dd MMM yyyy") : "no DOB")} · {p.Visits} visit(s) · registered {p.CreatedAt.ToLocalTime():dd MMM yyyy}";
}

/// <summary>FR-SYN-32: likely duplicate patients for an admin to confirm and merge (online; the merge syncs to every device).</summary>
public sealed partial class DuplicatesViewModel(AdminApi api, SyncCoordinator sync) : ViewModelBase
{
    public ObservableCollection<DuplicateRow> Pairs { get; } = [];
    public bool IsEmpty => Pairs.Count == 0;

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var pairs = await api.GetDuplicatesAsync();
        Pairs.Clear();
        foreach (var p in pairs) Pairs.Add(new DuplicateRow(p));
        OnPropertyChanged(nameof(IsEmpty));
    });

    [RelayCommand]
    private Task KeepFirstAsync(DuplicateRow row) => MergeAsync(row.Pair.A, row.Pair.B);

    [RelayCommand]
    private Task KeepSecondAsync(DuplicateRow row) => MergeAsync(row.Pair.B, row.Pair.A);

    private async Task MergeAsync(PatientBrief survivor, PatientBrief duplicate)
    {
        var ok = await Ui.ConfirmAsync("Merge patients",
            $"Keep {survivor.FullName} (MRN {survivor.Mrn}) and move every visit, bill and file of MRN {duplicate.Mrn} to it? This cannot be undone.", "Merge");
        if (!ok) return;
        var reason = await Ui.PromptAsync("Merge patients", "Reason (optional)", "Same person registered twice");
        MergeResultDto? result = null;
        if (await RunAsync(async () => result = await api.MergePatientsAsync(new MergePatientsRequest(survivor.Id, duplicate.Id, reason))))
        {
            var moved = result!.Moved.Where(m => m.Value > 0).Select(m => $"{m.Value} {m.Key}");
            await Ui.AlertAsync("Merged", $"Moved: {string.Join(", ", moved.DefaultIfEmpty("nothing"))}.");
            sync.RequestSync(manual: true);
            await LoadAsync();
        }
    }
}
