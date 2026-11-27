namespace Clinic.App.Services;

/// <summary>
/// Theme colours for code-built visuals (chips, bars). Reads the same tokens as the XAML in Resources/Styles/Colors.xaml:
/// "Success" in light mode, "SuccessDark" in dark mode when that key exists.
/// </summary>
public static class ThemeColors
{
    public static bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;

    public static Color Get(string key)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return Colors.Gray;
        if (IsDark && resources.TryGetValue(key + "Dark", out var dark) && dark is Color d) return d;
        return resources.TryGetValue(key, out var value) && value is Color c ? c : Colors.Gray;
    }

    /// <summary>Tone name → (background, text) for a status chip: success, warning, danger, info, neutral.</summary>
    public static (Color Background, Color Text) Chip(string tone) => tone switch
    {
        "success" => (Get("SuccessSoft"), Get("Success")),
        "warning" => (Get("WarningSoft"), Get("Warning")),
        "danger" => (Get("DangerSoft"), Get("Danger")),
        "info" => (Get("InfoSoft"), Get("Info")),
        _ => (IsDark ? Get("TonalDark") : Get("TonalLight"), IsDark ? Get("TextSecondaryDark") : Get("TextSecondary")),
    };
}
