namespace Clinic.Shared.Contracts;

/// <summary>Route constants shared by the API controllers and the app's HTTP client.</summary>
public static class ApiRoutes
{
    public const string Base = "api/v1";

    public static class Auth
    {
        public const string Root = Base + "/auth";
        public const string Login = Root + "/login";
        public const string Refresh = Root + "/refresh";
        public const string Logout = Root + "/logout";
        public const string ChangePassword = Root + "/change-password";
        public const string Me = Root + "/me";
    }

    public const string Tenants = Base + "/tenants";
    public const string Branches = Base + "/branches";
    public const string Users = Base + "/users";
    public const string Roles = Base + "/roles";
    public const string Permissions = Base + "/permissions";
    public const string Devices = Base + "/devices";

    /// <summary>ProblemDetails "type" values the app reacts to.</summary>
    public static class Problems
    {
        public const string DeviceRevoked = "https://clinic.app/problems/device-revoked";
        public const string LockedOut = "https://clinic.app/problems/locked-out";
    }
}
