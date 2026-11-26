namespace Clinic.App.Services;

/// <summary>Non-secret settings kept in Preferences.</summary>
public sealed class AppSettings
{
    private const string ServerUrlKey = "server_url";
    private const string TenantCodeKey = "tenant_code";
    private const string UserNameKey = "last_user";

    public static string DefaultServerUrl =>
        DeviceInfo.Platform == DevicePlatform.Android
            ? "http://10.0.2.2:5272"      // Android emulator → host machine
            : "http://localhost:5272";

    public string ServerUrl
    {
        get => Preferences.Default.Get(ServerUrlKey, DefaultServerUrl);
        set => Preferences.Default.Set(ServerUrlKey, value.Trim().TrimEnd('/'));
    }

    public string LastTenantCode
    {
        get => Preferences.Default.Get(TenantCodeKey, "");
        set => Preferences.Default.Set(TenantCodeKey, value);
    }

    public string LastUserName
    {
        get => Preferences.Default.Get(UserNameKey, "");
        set => Preferences.Default.Set(UserNameKey, value);
    }
}
