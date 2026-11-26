using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Shared.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

/// <summary>§5.1 / FR-DYN-14: lookup lists. Changes reach devices through sync.</summary>
public sealed partial class LookupsViewModel(AdminApi api) : ViewModelBase
{
    public ObservableCollection<LookupListDto> Lists { get; } = [];

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var lists = await api.GetLookupListsAsync();
        Lists.Clear();
        foreach (var l in lists) Lists.Add(l);
    });

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = await Ui.PromptAsync("New list", "List name (e.g. Wards)");
        if (string.IsNullOrWhiteSpace(name)) return;
        var suggested = string.Join("_", new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray())
            .Split('_', StringSplitOptions.RemoveEmptyEntries));
        var key = await Ui.PromptAsync("New list", "Key used by forms (lower_snake_case, cannot change later)", suggested);
        if (string.IsNullOrWhiteSpace(key)) return;

        string? parent = null;
        var parentChoice = await Shell.Current.DisplayActionSheetAsync("Parent list (for cascading dropdowns)", "Cancel", null,
            ["None", .. Lists.Select(l => l.Key)]);
        if (parentChoice is null or "Cancel") return;
        if (parentChoice != "None") parent = parentChoice;

        LookupListDto? created = null;
        if (await RunAsync(async () => created = await api.CreateLookupListAsync(new SaveLookupListRequest(key.Trim(), name.Trim(), parent))))
            await Ui.GoToAsync(Routes.LookupEdit, new Dictionary<string, object> { ["listId"] = created!.Id });
    }

    [RelayCommand]
    private Task OpenAsync(LookupListDto list) =>
        Ui.GoToAsync(Routes.LookupEdit, new Dictionary<string, object> { ["listId"] = list.Id });
}

public sealed record LookupItemRow(LookupItemDto Item)
{
    public string Title => Item.LabelUr is { } ur ? $"{Item.LabelEn}  ·  {ur}" : Item.LabelEn;
    public string Subtitle => $"{Item.Code}" + (Item.ParentCode is { } p ? $" · under {p}" : "") + (Item.IsActive ? "" : " · inactive");
}

public sealed partial class LookupEditViewModel(AdminApi api) : ViewModelBase, IQueryAttributable
{
    private Guid _listId;
    private LookupListDetailDto? _list;

    public ObservableCollection<LookupItemRow> Items { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "List";
    [ObservableProperty] public partial string? Info { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("listId", out var v) && v is Guid id) _listId = id;
        LoadCommand.Execute(null);
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        _list = await api.GetLookupListAsync(_listId);
        Title = _list.Name;
        Info = $"Key: {_list.Key}" + (_list.ParentListKey is { } p ? $" · items belong to a '{p}' item" : "") + (_list.IsSystem ? " · built-in" : "");
        Items.Clear();
        foreach (var i in _list.Items) Items.Add(new LookupItemRow(i));
    });

    [RelayCommand]
    private Task AddAsync() => EditItemAsync(null);

    [RelayCommand]
    private Task EditAsync(LookupItemRow row) => EditItemAsync(row.Item);

    private async Task EditItemAsync(LookupItemDto? item)
    {
        var code = item?.Code ?? await Ui.PromptAsync("New item", "Code (stored on records, cannot change later)");
        if (string.IsNullOrWhiteSpace(code)) return;
        var en = await Ui.PromptAsync("Item", "English label", item?.LabelEn);
        if (string.IsNullOrWhiteSpace(en)) return;
        var ur = await Ui.PromptAsync("Item", "Urdu label (optional)", item?.LabelUr);
        if (ur is null) return;

        string? parent = item?.ParentCode;
        if (_list?.ParentListKey is not null)
        {
            parent = await Ui.PromptAsync("Item", $"Parent code from '{_list.ParentListKey}'", item?.ParentCode);
            if (parent is null) return;
        }

        var active = item?.IsActive ?? true;
        if (item is not null)
        {
            var choice = await Shell.Current.DisplayActionSheetAsync("Status", "Cancel", null, "Active", "Inactive (hidden on devices)");
            if (choice is null or "Cancel") return;
            active = choice == "Active";
        }

        if (await RunAsync(() => api.SaveLookupItemAsync(_listId, item?.Id,
                new SaveLookupItemRequest(code.Trim(), en.Trim(), string.IsNullOrWhiteSpace(ur) ? null : ur.Trim(),
                    string.IsNullOrWhiteSpace(parent) ? null : parent.Trim(), item?.SortOrder ?? (Items.Count + 1) * 10, active))))
            await LoadAsync();
    }

    /// <summary>FR-DYN-14: import from a CSV file (Excel: Save As → CSV UTF-8).</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "CSV with code,labelEn,labelUr,parentCode,sortOrder" });
        if (file is null) return;

        ImportLookupItemsResult? result = null;
        var ok = await RunAsync(async () =>
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            result = await api.ImportLookupItemsAsync(_listId, await reader.ReadToEndAsync());
        });
        if (!ok) return;

        await Ui.AlertAsync("Import", $"{result!.Added} added, {result.Updated} updated." +
                                       (result.Errors.Count > 0 ? "\n\nSkipped:\n" + string.Join("\n", result.Errors.Take(15)) : ""));
        await LoadAsync();
    }
}
