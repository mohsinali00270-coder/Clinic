using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record DeviceRow(DeviceDto Device, bool IsThisDevice, bool CanManage)
{
    public string Title => IsThisDevice ? $"{Device.Name} (this device)" : Device.Name;
    public string Subtitle => $"{Device.DeviceCode} · {Device.Platform} {Device.Model} · v{Device.AppVersion}";
    public string LastSeen => Device.LastSeenAt is { } t ? $"Last seen {t.ToLocalTime():g}" : "Never seen";
    public string Status => Device.IsRevoked ? "Revoked" : "Active";
    public bool CanRevoke => CanManage && !Device.IsRevoked && !IsThisDevice;
}

public sealed partial class DevicesViewModel(AdminApi api, SessionStore session, DeviceIdentity identity) : ViewModelBase
{
    public ObservableCollection<DeviceRow> Devices { get; } = [];

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var canManage = session.Has(Permissions.Devices.Manage);
        var items = await api.GetDevicesAsync();
        Devices.Clear();
        foreach (var d in items.OrderBy(d => d.IsRevoked).ThenBy(d => d.DeviceCode))
            Devices.Add(new DeviceRow(d, d.Id == identity.DeviceId, canManage));
    });

    [RelayCommand]
    private async Task RevokeAsync(DeviceRow row)
    {
        if (!row.CanRevoke) return;
        var confirmed = await Ui.ConfirmAsync("Revoke device",
            $"Revoke \"{row.Device.Name}\"? It will be signed out and its local data wiped on next contact.", "Revoke");
        if (!confirmed) return;

        if (await RunAsync(() => api.RevokeDeviceAsync(row.Device.Id)))
            await LoadAsync();
    }

    [RelayCommand]
    private async Task RenameAsync(DeviceRow row)
    {
        if (!row.CanManage) return;
        var name = await Ui.PromptAsync("Rename device", "Device name", row.Device.Name);
        if (string.IsNullOrWhiteSpace(name) || name == row.Device.Name) return;

        if (await RunAsync(() => api.RenameDeviceAsync(row.Device.Id, name)))
            await LoadAsync();
    }
}
