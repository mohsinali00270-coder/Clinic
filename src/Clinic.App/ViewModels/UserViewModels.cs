using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record UserRow(UserDto User, string RolesText, string BranchesText)
{
    public string StatusText => User.IsLockedOut ? "Locked" : User.IsActive ? "Active" : "Disabled";
    public string Initials => string.Concat(User.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0])).ToUpperInvariant();
}

public sealed partial class UsersViewModel(AdminApi api, SessionStore session) : ViewModelBase
{
    public ObservableCollection<UserRow> Users { get; } = [];

    [ObservableProperty] public partial string? Search { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public bool CanManage => session.Has(Permissions.Users.Manage);

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var rolesTask = session.Has(Permissions.Roles.View) ? api.GetRolesAsync() : Task.FromResult(new List<RoleDto>());
        var branchesTask = session.Has(Permissions.Branches.View) ? api.GetBranchesAsync() : Task.FromResult(new List<BranchDto>());
        var usersTask = api.GetUsersAsync(Search);
        await Task.WhenAll(rolesTask, branchesTask, usersTask);

        var roles = rolesTask.Result.ToDictionary(r => r.Id, r => r.Name);
        var branches = branchesTask.Result.ToDictionary(b => b.Id, b => b.Code);
        var page = usersTask.Result;

        Users.Clear();
        foreach (var u in page.Items)
        {
            Users.Add(new UserRow(u,
                string.Join(", ", u.RoleIds.Select(id => roles.GetValueOrDefault(id, "?"))),
                string.Join(", ", u.BranchIds.Select(id => branches.GetValueOrDefault(id, "?")))));
        }
        Summary = $"{page.TotalCount} user(s)";
        OnPropertyChanged(nameof(CanManage));
    });

    [RelayCommand]
    private Task AddAsync() => Ui.GoToAsync(Routes.UserEdit);

    [RelayCommand]
    private Task EditAsync(UserRow row) =>
        CanManage ? Ui.GoToAsync(Routes.UserEdit, new Dictionary<string, object> { ["userId"] = row.User.Id }) : Task.CompletedTask;
}

public sealed partial class UserEditViewModel(AdminApi api, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid? _id;

    public ObservableCollection<SelectableOption> Roles { get; } = [];
    public ObservableCollection<SelectableOption> Branches { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew), nameof(IsExisting))]
    public partial string Title { get; set; } = "New user";

    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string FullName { get; set; } = "";
    [ObservableProperty] public partial string? Email { get; set; }
    [ObservableProperty] public partial string? Phone { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool IsActive { get; set; } = true;
    [ObservableProperty] public partial bool IsLockedOut { get; set; }

    public bool IsNew => _id is null;
    public bool IsExisting => _id is not null;
    public bool CanResetPassword => IsExisting && session.Has(Permissions.Users.ResetPassword);

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _id = query.TryGetValue("userId", out var v) && v is Guid id ? id : null;
        Title = _id is null ? "New user" : "Edit user";
        OnPropertyChanged(nameof(CanResetPassword));
        LoadCommand.Execute(null);
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var rolesTask = api.GetRolesAsync();
        var branchesTask = api.GetBranchesAsync();
        var userTask = _id is { } id ? api.GetUserAsync(id) : Task.FromResult<UserDto?>(null)!;
        await Task.WhenAll(rolesTask, branchesTask, userTask);
        var user = userTask.Result;

        Roles.Clear();
        foreach (var r in rolesTask.Result)
            Roles.Add(new SelectableOption(r.Id, r.Name, r.Description) { IsSelected = user?.RoleIds.Contains(r.Id) == true });

        Branches.Clear();
        foreach (var b in branchesTask.Result.Where(b => b.IsActive || user?.BranchIds.Contains(b.Id) == true))
            Branches.Add(new SelectableOption(b.Id, $"{b.Name} ({b.Code})") { IsSelected = user?.BranchIds.Contains(b.Id) == true });

        if (user is not null)
        {
            UserName = user.UserName;
            FullName = user.FullName;
            Email = user.Email;
            Phone = user.Phone;
            IsActive = user.IsActive;
            IsLockedOut = user.IsLockedOut;
        }
        else if (Branches.Count == 1)
        {
            Branches[0].IsSelected = true;
        }
    });

    [RelayCommand]
    private async Task SaveAsync()
    {
        var roleIds = Roles.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        var branchIds = Branches.Where(b => b.IsSelected).Select(b => b.Id).ToList();

        var ok = await RunAsync(async () =>
        {
            if (_id is { } id)
                await api.UpdateUserAsync(id, new UpdateUserRequest(FullName, Email, Phone, IsActive, roleIds, branchIds));
            else
                await api.CreateUserAsync(new CreateUserRequest(UserName, FullName, Email, Phone, Password, roleIds, branchIds));
        });
        if (ok) await Ui.GoBackAsync();
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (_id is not { } id) return;
        var password = await Ui.PromptAsync("Reset password", $"New password for {UserName}", password: true);
        if (string.IsNullOrEmpty(password)) return;

        if (await RunAsync(() => api.ResetPasswordAsync(id, password)))
        {
            IsLockedOut = false;
            await Ui.AlertAsync("Password reset", $"{FullName} must use the new password. Their other sessions were signed out.");
        }
    }

    [RelayCommand]
    private Task CancelAsync() => Ui.GoBackAsync();
}
