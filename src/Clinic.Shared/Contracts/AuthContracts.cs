using Clinic.Shared.Security;

namespace Clinic.Shared.Contracts;

/// <summary>Leave TenantCode empty to sign in as the platform Super Admin.</summary>
public sealed record LoginRequest(string? TenantCode, string UserName, string Password, Guid? DeviceId = null);

public sealed record RefreshRequest(string RefreshToken, Guid? DeviceId = null);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    MeDto Me);

public sealed record GrantedPermission(string Name, PermissionScope Scope);

public sealed record MeDto(
    Guid UserId,
    string UserName,
    string FullName,
    Guid? TenantId,
    string? TenantCode,
    string? TenantName,
    bool IsSuperAdmin,
    IReadOnlyList<BranchSummaryDto> Branches,
    IReadOnlyList<string> Roles,
    IReadOnlyList<GrantedPermission> Permissions,
    int OfflineGraceDays);

public sealed record BranchSummaryDto(Guid Id, string Code, string Name);
