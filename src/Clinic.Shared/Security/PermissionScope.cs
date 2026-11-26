namespace Clinic.Shared.Security;

/// <summary>FR-AUTH-02: how far a granted permission reaches.</summary>
public enum PermissionScope
{
    /// <summary>Only records the user created or is assigned to (e.g. a doctor's own patients).</summary>
    OwnRecords = 0,

    /// <summary>Records in the branches the user is assigned to.</summary>
    OwnBranch = 1,

    /// <summary>Every branch in the tenant.</summary>
    All = 2
}

public static class ClinicClaimTypes
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string TenantCode = "tcode";
    public const string UserName = "uname";
    public const string FullName = "name";
    public const string Branch = "br";
    public const string DeviceId = "did";
    public const string SuperAdmin = "sa";
    public const string Role = "role";

    /// <summary>Value format: "Permission.Name:Scope", e.g. "Patients.Create:OwnBranch".</summary>
    public const string Permission = "perm";
}
