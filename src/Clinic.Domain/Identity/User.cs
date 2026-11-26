using Clinic.Domain.Common;
using Clinic.Shared.Security;

namespace Clinic.Domain.Identity;

/// <summary>
/// A staff user. Super Admins (SaaS owner) have no tenant; they are kept in a separate
/// table so the tenant filter never has to special-case them.
/// </summary>
public class User : TenantEntity
{
    public string UserName { get; set; } = "";
    public string NormalizedUserName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;

    /// <summary>FR-SEC-04: lockout after 5 failed attempts.</summary>
    public int AccessFailedCount { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public List<UserRole> Roles { get; set; } = [];
    public List<UserBranch> Branches { get; set; } = [];

    public bool IsLockedOut(DateTime utcNow) => LockoutEnd is { } end && end > utcNow;
}

public class PlatformAdmin : Entity
{
    public string UserName { get; set; } = "";
    public string NormalizedUserName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int AccessFailedCount { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class Role : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Seeded roles can be edited but not deleted.</summary>
    public bool IsSystem { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
    public PermissionScope Scope { get; set; } = PermissionScope.All;
}

public class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class UserBranch
{
    public Guid UserId { get; set; }
    public Guid BranchId { get; set; }
}

/// <summary>Rotating refresh token (FR-API-02). Only a SHA-256 hash is stored.</summary>
public class RefreshToken : Entity
{
    /// <summary>Null for platform admins.</summary>
    public Guid? TenantId { get; set; }
    public Guid SubjectId { get; set; }
    public bool IsPlatformAdmin { get; set; }
    public Guid? DeviceId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }

    /// <summary>All tokens descended from one login share a family; reuse revokes the family.</summary>
    public Guid FamilyId { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAt is null && ExpiresAt > utcNow;
}
