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

    /// <summary>Appearance: "system" (follow the device), "light" or "dark".</summary>
    public string Theme
    {
        get => Preferences.Default.Get("app_theme", "system");
        set
        {
            Preferences.Default.Set("app_theme", value);
            ApplyTheme(value);
        }
    }

    public static void ApplyTheme(string theme)
    {
        if (Application.Current is { } app)
            app.UserAppTheme = theme switch { "light" => AppTheme.Light, "dark" => AppTheme.Dark, _ => AppTheme.Unspecified };
    }

    /// <summary>FR-SEC-05: minutes without use before the PIN screen covers the app.</summary>
    public int AutoLockMinutes
    {
        get => Preferences.Default.Get("auto_lock_minutes", 5);
        set => Preferences.Default.Set("auto_lock_minutes", Math.Clamp(value, 1, 60));
    }

    /// <summary>FR-SEC-01: SHA-256 SPKI pins (base64, separated by ';'). Empty: the OS trust store alone decides.</summary>
    public IReadOnlyCollection<string> CertificatePins
    {
        get
        {
            var configured = Preferences.Default.Get("certificate_pins", BuildPins);
            return configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        }
        set => Preferences.Default.Set("certificate_pins", string.Join(';', value));
    }

    /// <summary>Pins baked in at build time for production (-p:CertificatePins=...); empty in development.</summary>
    private const string BuildPins = "";

    public string LastUserName
    {
        get => Preferences.Default.Get(UserNameKey, "");
        set => Preferences.Default.Set(UserNameKey, value);
    }
}
