using Clinic.Shared.Contracts;

namespace Clinic.App.Services;

public sealed class AuthService(ApiClient api, SessionStore session, DeviceIdentity device, AppSettings settings)
{
    public async Task LoginAsync(string tenantCode, string userName, string password, CancellationToken ct = default)
    {
        var token = await api.LoginAsync(new LoginRequest(tenantCode, userName, password, device.DeviceId), ct);
        await session.SaveAsync(token);

        settings.LastTenantCode = tenantCode;
        settings.LastUserName = userName;

        // FR-OFF-30: register (or refresh) this device right after every online login.
        var registered = await api.PostAsync<DeviceDto>($"{ApiRoutes.Devices}/register",
            device.BuildRegistration(token.Me.Branches.FirstOrDefault()?.Id), ct);
        device.DeviceCode = registered.DeviceCode;
    }

    public async Task LogoutAsync()
    {
        if (session.RefreshToken is { } refresh)
        {
            try { await api.PostAsync(ApiRoutes.Auth.Logout, new LogoutRequest(refresh)); }
            catch (ApiException) { /* offline or already expired: clearing locally is enough */ }
        }
        await session.ClearAsync();
    }

    /// <summary>Refreshes the cached profile/permissions when online; keeps the cached copy otherwise.</summary>
    public async Task RefreshProfileAsync(CancellationToken ct = default)
    {
        try
        {
            session.UpdateMe(await api.GetAsync<MeDto>(ApiRoutes.Auth.Me, ct));
        }
        catch (ApiException ex) when (ex.IsNetworkError) { }
    }

    public Task ChangePasswordAsync(string current, string next) =>
        api.PostAsync(ApiRoutes.Auth.ChangePassword, new ChangePasswordRequest(current, next));
}
