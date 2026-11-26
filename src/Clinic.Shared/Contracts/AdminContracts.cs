using Clinic.Shared.Security;

namespace Clinic.Shared.Contracts;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

// ---------- Tenants (Super Admin) ----------

public sealed record TenantDto(Guid Id, string Code, string Name, bool IsActive, int OfflineGraceDays, DateTime CreatedAt);

/// <summary>Creates the tenant, a first branch and a Tenant Admin user with the default roles.</summary>
public sealed record CreateTenantRequest(
    string Code,
    string Name,
    string FirstBranchCode,
    string FirstBranchName,
    string AdminUserName,
    string AdminFullName,
    string AdminPassword);

public sealed record UpdateTenantRequest(string Name, bool IsActive, int OfflineGraceDays);

// ---------- Branches ----------

public sealed record BranchDto(Guid Id, string Code, string Name, string? Address, string? Phone, bool IsActive);

public sealed record SaveBranchRequest(string Code, string Name, string? Address, string? Phone, bool IsActive = true);

// ---------- Roles ----------

public sealed record RoleDto(Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<GrantedPermission> Permissions);

public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<GrantedPermission> Permissions);

// ---------- Users ----------

public sealed record UserDto(
    Guid Id,
    string UserName,
    string FullName,
    string? Email,
    string? Phone,
    bool IsActive,
    bool IsLockedOut,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> BranchIds);

public sealed record CreateUserRequest(
    string UserName,
    string FullName,
    string? Email,
    string? Phone,
    string Password,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> BranchIds);

public sealed record UpdateUserRequest(
    string FullName,
    string? Email,
    string? Phone,
    bool IsActive,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> BranchIds);

public sealed record ResetPasswordRequest(string NewPassword);

// ---------- Devices ----------

public sealed record RegisterDeviceRequest(
    Guid DeviceId,
    string Name,
    string Platform,
    string? Model,
    string AppVersion,
    Guid? BranchId);

public sealed record DeviceDto(
    Guid Id,
    string DeviceCode,
    string Name,
    string Platform,
    string? Model,
    string AppVersion,
    Guid? BranchId,
    string? RegisteredBy,
    DateTime RegisteredAt,
    DateTime? LastSeenAt,
    bool IsRevoked,
    DateTime? LastSyncAt = null,
    int LastPushCount = 0,
    int LastPullCount = 0);

public sealed record RenameDeviceRequest(string Name);
