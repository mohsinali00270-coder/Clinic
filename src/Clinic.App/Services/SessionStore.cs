using System.Text.Json;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;

namespace Clinic.App.Services;

/// <summary>
/// Holds the signed-in session. Tokens live in platform secure storage (Keychain / Keystore / DPAPI);
/// the profile and permission set are cached so the UI can enforce permissions offline (FR-AUTH-04).
/// </summary>
public sealed class SessionStore
{
    private const string AccessKey = "access_token";
    private const string RefreshKey = "refresh_token";
    private const string AccessExpiresKey = "access_expires";
    private const string MeKey = "me";
    private const string LastOnlineLoginKey = "last_online_login";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Dictionary<string, PermissionScope> _permissions = new();

    public MeDto? Me { get; private set; }
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTime AccessTokenExpiresAt { get; private set; }

    public bool IsSignedIn => Me is not null && RefreshToken is not null;

    public event EventHandler? Changed;

    public async Task SaveAsync(TokenResponse token)
    {
        AccessToken = token.AccessToken;
        RefreshToken = token.RefreshToken;
        AccessTokenExpiresAt = token.AccessTokenExpiresAt;
        SetMe(token.Me);

        await SecureStorage.Default.SetAsync(AccessKey, token.AccessToken);
        await SecureStorage.Default.SetAsync(RefreshKey, token.RefreshToken);
        Preferences.Default.Set(AccessExpiresKey, token.AccessTokenExpiresAt.ToString("O"));
        Preferences.Default.Set(LastOnlineLoginKey, DateTime.UtcNow.ToString("O"));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateMe(MeDto me)
    {
        SetMe(me);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores the last session on app start. Returns false if there is none.</summary>
    public async Task<bool> RestoreAsync()
    {
        try
        {
            RefreshToken = await SecureStorage.Default.GetAsync(RefreshKey);
            AccessToken = await SecureStorage.Default.GetAsync(AccessKey);
        }
        catch
        {
            // Secure storage can be reset by the OS (e.g. after a backup restore); start clean.
            await ClearAsync();
            return false;
        }

        AccessTokenExpiresAt = DateTime.TryParse(Preferences.Default.Get(AccessExpiresKey, ""), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var exp) ? exp : DateTime.MinValue;

        var meJson = Preferences.Default.Get(MeKey, "");
        if (!string.IsNullOrEmpty(meJson))
            SetMe(JsonSerializer.Deserialize<MeDto>(meJson, Json));

        return IsSignedIn;
    }

    public Task ClearAsync()
    {
        AccessToken = RefreshToken = null;
        Me = null;
        _permissions = new();
        SecureStorage.Default.Remove(AccessKey);
        SecureStorage.Default.Remove(RefreshKey);
        Preferences.Default.Remove(AccessExpiresKey);
        Preferences.Default.Remove(MeKey);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public DateTime? LastOnlineLogin =>
        DateTime.TryParse(Preferences.Default.Get(LastOnlineLoginKey, ""), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>FR-AUTH-01: the app hides/disables UI for permissions the user does not hold.</summary>
    public bool Has(string permission) => _permissions.ContainsKey(permission);

    public PermissionScope? ScopeOf(string permission) =>
        _permissions.TryGetValue(permission, out var s) ? s : null;

    private void SetMe(MeDto? me)
    {
        Me = me;
        _permissions = me?.Permissions.ToDictionary(p => p.Name, p => p.Scope) ?? new();
        if (me is not null) Preferences.Default.Set(MeKey, JsonSerializer.Serialize(me, Json));
    }
}
