using Clinic.Domain.Common;

namespace Clinic.Domain.Tenancy;

/// <summary>A clinic organisation on the SaaS platform. Not tenant-filtered itself.</summary>
public class Tenant : Entity, ISoftDelete
{
    /// <summary>Short unique code typed at login (e.g. "citycare").</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;

    /// <summary>FR-AUTH-05: days a device may stay offline before it must log in online again.</summary>
    public int OfflineGraceDays { get; set; } = 7;

    /// <summary>Which default-permission additions have been applied to this tenant's built-in roles.</summary>
    public int PermissionCatalogVersion { get; set; }

    public bool IsDeleted { get; set; }
}

/// <summary>Pulled by every device in the tenant (config first, FR-SYN-22). Edited only through the admin API.</summary>
public class Branch : Sync.SyncEntity
{
    /// <summary>Short code used in offline numbers ({BranchCode}-{DeviceCode}-{Seq}).</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}
