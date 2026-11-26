using Clinic.Shared.Contracts;

namespace Clinic.App.Services;

/// <summary>
/// FR-OFF-30: a GUID generated once per install. It becomes the device's primary key on the server
/// and later prefixes every offline-created record's outbox entries.
/// </summary>
public sealed class DeviceIdentity
{
    private const string DeviceIdKey = "device_id";
    private const string DeviceCodeKey = "device_code";

    public Guid DeviceId
    {
        get
        {
            var stored = Preferences.Default.Get(DeviceIdKey, "");
            if (Guid.TryParse(stored, out var id)) return id;
            id = Guid.NewGuid();
            Preferences.Default.Set(DeviceIdKey, id.ToString());
            return id;
        }
    }

    public string? DeviceCode
    {
        get => Preferences.Default.Get<string?>(DeviceCodeKey, null);
        set => Preferences.Default.Set(DeviceCodeKey, value);
    }

    public static string AppVersion => AppInfo.Current.VersionString;

    public RegisterDeviceRequest BuildRegistration(Guid? branchId) => new(
        DeviceId,
        string.IsNullOrWhiteSpace(Microsoft.Maui.Devices.DeviceInfo.Current.Name)
            ? $"{Microsoft.Maui.Devices.DeviceInfo.Current.Platform} device"
            : Microsoft.Maui.Devices.DeviceInfo.Current.Name,
        Microsoft.Maui.Devices.DeviceInfo.Current.Platform.ToString(),
        $"{Microsoft.Maui.Devices.DeviceInfo.Current.Manufacturer} {Microsoft.Maui.Devices.DeviceInfo.Current.Model}".Trim(),
        AppVersion,
        branchId);
}
