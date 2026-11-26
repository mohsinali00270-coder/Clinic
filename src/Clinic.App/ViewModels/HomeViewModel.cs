using Clinic.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly SessionStore _session;
    private readonly AuthService _auth;
    private readonly DeviceIdentity _device;
    private readonly OfflinePinService _pins;

    public HomeViewModel(SessionStore session, AuthService auth, DeviceIdentity device, OfflinePinService pins, SyncCoordinator sync)
    {
        _session = session;
        _auth = auth;
        _device = device;
        _pins = pins;
        Sync = sync;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        Refresh();
    }

    /// <summary>Bound directly for the live sync status line and badge (FR-OFF-12, FR-OFF-20).</summary>
    public SyncCoordinator Sync { get; }

    [ObservableProperty] public partial string Greeting { get; set; } = "";
    [ObservableProperty] public partial string TenantName { get; set; } = "";
    [ObservableProperty] public partial string Branches { get; set; } = "";
    [ObservableProperty] public partial string Roles { get; set; } = "";
    [ObservableProperty] public partial string DeviceInfoText { get; set; } = "";
    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial bool CanBranches { get; set; }
    [ObservableProperty] public partial bool CanUsers { get; set; }
    [ObservableProperty] public partial bool CanDevices { get; set; }
    [ObservableProperty] public partial bool CanNotes { get; set; }
    [ObservableProperty] public partial bool CanForms { get; set; }
    [ObservableProperty] public partial bool CanPatients { get; set; }
    [ObservableProperty] public partial bool CanAppointments { get; set; }
    [ObservableProperty] public partial bool CanQueue { get; set; }
    [ObservableProperty] public partial bool CanDoctors { get; set; }
    [ObservableProperty] public partial bool NeedsPin { get; set; }
    [ObservableProperty] public partial int PermissionCount { get; set; }

    public void Refresh()
    {
        var me = _session.Me;
        Greeting = me is null ? "" : $"Welcome, {me.FullName}";
        TenantName = me?.TenantName ?? "";
        Branches = me is null ? "" : string.Join(", ", me.Branches.Select(b => $"{b.Name} ({b.Code})"));
        Roles = me is null ? "" : string.Join(", ", me.Roles);
        PermissionCount = me?.Permissions.Count ?? 0;
        DeviceInfoText = $"Device {_device.DeviceCode ?? "not registered"} · app v{DeviceIdentity.AppVersion}";
        CanBranches = _session.Has(Permissions.Branches.View);
        CanUsers = _session.Has(Permissions.Users.View);
        CanDevices = _session.Has(Permissions.Devices.View);
        CanNotes = _session.Has(Permissions.Notes.View);
        CanForms = _session.Has(Permissions.DynamicFields.Manage);
        CanPatients = _session.Has(Permissions.Patients.View);
        CanAppointments = _session.Has(Permissions.Appointments.View);
        CanQueue = _session.Has(Permissions.Queue.Manage);
        CanDoctors = _session.Has(Permissions.Doctors.Manage) || _session.Has(Permissions.Scheduling.Manage);
        NeedsPin = me is not null && !_pins.HasPin(me.UserId);
        IsOnline = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
    }

    [RelayCommand]
    private async Task AppearingAsync()
    {
        await _auth.RefreshProfileAsync();
        Refresh();
    }

    [RelayCommand] private Task OpenBranchesAsync() => Ui.GoToAsync($"//{Routes.Branches}");
    [RelayCommand] private Task OpenUsersAsync() => Ui.GoToAsync($"//{Routes.Users}");
    [RelayCommand] private Task OpenDevicesAsync() => Ui.GoToAsync($"//{Routes.Devices}");
    [RelayCommand] private Task OpenNotesAsync() => Ui.GoToAsync($"//{Routes.Notes}");
    [RelayCommand] private Task OpenPatientsAsync() => Ui.GoToAsync($"//{Routes.Patients}");
    [RelayCommand] private Task OpenAppointmentsAsync() => Ui.GoToAsync($"//{Routes.Appointments}");
    [RelayCommand] private Task OpenQueueAsync() => Ui.GoToAsync($"//{Routes.Queue}");
    [RelayCommand] private Task OpenDoctorsAsync() => Ui.GoToAsync($"//{Routes.Doctors}");
    [RelayCommand] private Task OpenFormsAsync() => Ui.GoToAsync($"//{Routes.Forms}");
    [RelayCommand] private Task OpenSyncCenterAsync() => Ui.GoToAsync($"//{Routes.SyncCenter}");
    [RelayCommand] private void SyncNow() => Sync.RequestSync(manual: true);

    /// <summary>FR-AUTH-05: set the PIN used to unlock the app when there is no network.</summary>
    [RelayCommand]
    private async Task SetupPinAsync()
    {
        if (_session.Me is not { } me) return;
        var pin = await Shell.Current.DisplayPromptAsync("Offline PIN", "Choose a 4–8 digit PIN to unlock this device when offline.",
            "Next", "Cancel", maxLength: 8, keyboard: Keyboard.Numeric);
        if (pin is null) return;
        if (!OfflinePinService.IsValidFormat(pin))
        {
            await Ui.AlertAsync("Offline PIN", "The PIN must be 4 to 8 digits.");
            return;
        }
        var again = await Shell.Current.DisplayPromptAsync("Offline PIN", "Enter the PIN again.", "Save", "Cancel",
            maxLength: 8, keyboard: Keyboard.Numeric);
        if (again != pin)
        {
            if (again is not null) await Ui.AlertAsync("Offline PIN", "The PINs did not match.");
            return;
        }
        await _pins.SetPinAsync(me.UserId, pin);
        NeedsPin = false;
        await Ui.AlertAsync("Offline PIN", "PIN saved. Use it to unlock the app, even without internet.");
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        var current = await Ui.PromptAsync("Change password", "Current password", password: true);
        if (string.IsNullOrEmpty(current)) return;
        var next = await Ui.PromptAsync("Change password", "New password (8+ chars, upper, lower, digit)", password: true);
        if (string.IsNullOrEmpty(next)) return;

        if (await RunAsync(() => _auth.ChangePasswordAsync(current, next)))
            await Ui.AlertAsync("Password changed", "Other devices signed in with your account will need to sign in again.");
    }

    [RelayCommand]
    private Task LogoutAsync() => AppShell.Instance.SignOutAsync();

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => IsOnline = e.NetworkAccess == NetworkAccess.Internet);

    public void Dispose() => Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
}
