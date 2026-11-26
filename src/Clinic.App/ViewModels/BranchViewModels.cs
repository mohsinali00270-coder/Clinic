using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed partial class BranchesViewModel(AdminApi api, SessionStore session) : ViewModelBase
{
    public ObservableCollection<BranchDto> Branches { get; } = [];

    public bool CanManage => session.Has(Permissions.Branches.Manage);

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var items = await api.GetBranchesAsync();
        Branches.Clear();
        foreach (var b in items) Branches.Add(b);
        OnPropertyChanged(nameof(CanManage));
    });

    [RelayCommand]
    private Task AddAsync() => Ui.GoToAsync(Routes.BranchEdit);

    [RelayCommand]
    private Task EditAsync(BranchDto branch) =>
        CanManage ? Ui.GoToAsync(Routes.BranchEdit, new Dictionary<string, object> { ["branch"] = branch }) : Task.CompletedTask;
}

public sealed partial class BranchEditViewModel(AdminApi api) : ViewModelBase, IQueryAttributable
{
    private Guid? _id;

    [ObservableProperty] public partial string Title { get; set; } = "New branch";
    [ObservableProperty] public partial string Code { get; set; } = "";
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string? Address { get; set; }
    [ObservableProperty] public partial string? Phone { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; } = true;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("branch", out var value) || value is not BranchDto b) return;
        _id = b.Id;
        Title = $"Edit {b.Name}";
        Code = b.Code;
        Name = b.Name;
        Address = b.Address;
        Phone = b.Phone;
        IsActive = b.IsActive;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var request = new SaveBranchRequest(Code, Name, Address, Phone, IsActive);
        var ok = await RunAsync(async () =>
        {
            if (_id is { } id) await api.UpdateBranchAsync(id, request);
            else await api.CreateBranchAsync(request);
        });
        if (ok) await Ui.GoBackAsync();
    }

    [RelayCommand]
    private Task CancelAsync() => Ui.GoBackAsync();
}
