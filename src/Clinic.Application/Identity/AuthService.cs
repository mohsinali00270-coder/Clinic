using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Identity;
using Clinic.Domain.Tenancy;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Application.Identity;

public sealed class AuthService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    ICurrentUser currentUser,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    IValidator<ChangePasswordRequest> changePasswordValidator)
{
    private const string InvalidCredentials = "Invalid tenant, user name or password.";
    private readonly AuthOptions _options = options.Value;

    // A real PBKDF2 hash, verified when the user does not exist so timing does not reveal valid names.
    private static string? _dummyHash;

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
            throw new AuthFailedException(InvalidCredentials);

        var userName = Normalize.Key(request.UserName);

        if (string.IsNullOrWhiteSpace(request.TenantCode))
            return await LoginPlatformAdminAsync(userName, request.Password, ct);

        var code = request.TenantCode.Trim().ToLowerInvariant();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Code == code && t.IsActive, ct);
        if (tenant is null)
        {
            BurnHash(request.Password);
            throw new AuthFailedException(InvalidCredentials);
        }

        using var scope = db.BeginTenantScope(tenant.Id);

        var user = await LoadUserAsync(u => u.NormalizedUserName == userName, ct);
        if (user is null)
        {
            BurnHash(request.Password);
            throw new AuthFailedException(InvalidCredentials);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (user.IsLockedOut(now))
            throw new AuthFailedException("Account is locked. Try again later.", ApiRoutes.Problems.LockedOut);

        if (!hasher.Verify(user.PasswordHash, request.Password))
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= _options.MaxFailedAttempts)
            {
                user.LockoutEnd = now.AddMinutes(_options.LockoutMinutes);
                user.AccessFailedCount = 0;
            }
            await db.SaveChangesAsync(ct);
            throw new AuthFailedException(InvalidCredentials);
        }

        if (!user.IsActive)
            throw new AuthFailedException("This account is disabled.");

        await EnsureDeviceNotRevokedAsync(request.DeviceId, ct);

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = now;

        var response = await IssueAsync(UserSubject(user, tenant, request.DeviceId), tenant, Guid.NewGuid(), ct);
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = tokens.HashRefreshToken(request.RefreshToken ?? "");
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
                     ?? throw new AuthFailedException("Invalid refresh token.");

        if (stored.RevokedAt is not null)
        {
            // Reuse of a rotated token means it leaked: revoke the whole family.
            if (stored.ReplacedByHash is not null)
            {
                await db.RefreshTokens
                    .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            }
            throw new AuthFailedException("Refresh token has been revoked.");
        }

        if (stored.ExpiresAt <= now)
            throw new AuthFailedException("Refresh token has expired.");

        TokenSubject subject;
        Tenant? tenant = null;
        IDisposable? tenantScope = null;
        try
        {
            if (stored.IsPlatformAdmin)
            {
                var admin = await db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == stored.SubjectId, ct);
                if (admin is null || !admin.IsActive || IsLocked(admin.LockoutEnd, now))
                    throw new AuthFailedException("Account is not available.");
                subject = AdminSubject(admin);
            }
            else
            {
                tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == stored.TenantId && t.IsActive, ct)
                         ?? throw new AuthFailedException("Tenant is not available.");

                tenantScope = db.BeginTenantScope(tenant.Id);
                var user = await LoadUserAsync(u => u.Id == stored.SubjectId, ct);
                if (user is null || !user.IsActive || user.IsLockedOut(now))
                    throw new AuthFailedException("Account is not available.");

                await EnsureDeviceNotRevokedAsync(stored.DeviceId, ct);
                subject = UserSubject(user, tenant, stored.DeviceId);
            }

            // Permissions are re-read from the roles here, so role edits reach devices on next refresh (FR-AUTH-04).
            var response = await IssueAsync(subject, tenant, stored.FamilyId, ct);
            stored.RevokedAt = now;
            stored.ReplacedByHash = tokens.HashRefreshToken(response.RefreshToken);
            await db.SaveChangesAsync(ct);
            return response;
        }
        finally
        {
            tenantScope?.Dispose();
        }
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = tokens.HashRefreshToken(request.RefreshToken ?? "");
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null || stored.SubjectId != currentUser.UserId) return;

        await db.RefreshTokens
            .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        await changePasswordValidator.ValidateOrThrowAsync(request, ct);
        var userId = currentUser.UserId ?? throw new AuthFailedException("Not signed in.");

        if (currentUser.IsSuperAdmin)
        {
            var admin = await db.PlatformAdmins.FirstAsync(a => a.Id == userId, ct);
            if (!hasher.Verify(admin.PasswordHash, request.CurrentPassword))
                throw new RequestValidationException("currentPassword", "Current password is incorrect.");
            admin.PasswordHash = hasher.Hash(request.NewPassword);
        }
        else
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                       ?? throw new NotFoundException("User", userId);
            if (!hasher.Verify(user.PasswordHash, request.CurrentPassword))
                throw new RequestValidationException("currentPassword", "Current password is incorrect.");
            user.PasswordHash = hasher.Hash(request.NewPassword);
        }

        // Sign out every other session.
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.SubjectId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        await db.SaveChangesAsync(ct);
    }

    public async Task<MeDto> GetMeAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new AuthFailedException("Not signed in.");

        if (currentUser.IsSuperAdmin)
        {
            var admin = await db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == userId, ct)
                        ?? throw new AuthFailedException("Account is not available.");
            return BuildMe(AdminSubject(admin), null, [], ["Super Admin"]);
        }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == currentUser.TenantId, ct);
        var user = await LoadUserAsync(u => u.Id == userId, ct) ?? throw new AuthFailedException("Account is not available.");
        var subject = UserSubject(user, tenant, currentUser.DeviceId);
        var branches = await BranchSummariesAsync(subject.BranchIds, ct);
        return BuildMe(subject, tenant, branches, user.Roles.Select(r => r.Role!.Name).ToList());
    }

    // ---------------------------------------------------------------------

    private async Task<TokenResponse> LoginPlatformAdminAsync(string userName, string password, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var admin = await db.PlatformAdmins.FirstOrDefaultAsync(a => a.NormalizedUserName == userName, ct);
        if (admin is null)
        {
            BurnHash(password);
            throw new AuthFailedException(InvalidCredentials);
        }

        if (IsLocked(admin.LockoutEnd, now))
            throw new AuthFailedException("Account is locked. Try again later.", ApiRoutes.Problems.LockedOut);

        if (!hasher.Verify(admin.PasswordHash, password))
        {
            admin.AccessFailedCount++;
            if (admin.AccessFailedCount >= _options.MaxFailedAttempts)
            {
                admin.LockoutEnd = now.AddMinutes(_options.LockoutMinutes);
                admin.AccessFailedCount = 0;
            }
            await db.SaveChangesAsync(ct);
            throw new AuthFailedException(InvalidCredentials);
        }

        if (!admin.IsActive) throw new AuthFailedException("This account is disabled.");

        admin.AccessFailedCount = 0;
        admin.LockoutEnd = null;
        admin.LastLoginAt = now;

        var response = await IssueAsync(AdminSubject(admin), null, Guid.NewGuid(), ct);
        await db.SaveChangesAsync(ct);
        return response;
    }

    private async Task<TokenResponse> IssueAsync(TokenSubject subject, Tenant? tenant, Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var (access, accessExpires) = tokens.CreateAccessToken(subject);
        var refresh = tokens.CreateRefreshToken();
        var refreshExpires = now.AddDays(_options.RefreshTokenDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            TenantId = subject.TenantId,
            SubjectId = subject.UserId,
            IsPlatformAdmin = subject.IsSuperAdmin,
            DeviceId = subject.DeviceId,
            TokenHash = tokens.HashRefreshToken(refresh),
            ExpiresAt = refreshExpires,
            FamilyId = familyId,
        });

        var branches = await BranchSummariesAsync(subject.BranchIds, ct);
        var roles = subject.IsSuperAdmin ? new List<string> { "Super Admin" } : await RoleNamesAsync(subject.UserId, ct);
        return new TokenResponse(access, accessExpires, refresh, refreshExpires, BuildMe(subject, tenant, branches, roles));
    }

    private async Task EnsureDeviceNotRevokedAsync(Guid? deviceId, CancellationToken ct)
    {
        if (deviceId is null) return;
        var revoked = await db.Devices.AnyAsync(d => d.Id == deviceId && d.IsRevoked, ct);
        if (revoked)
            throw new AuthFailedException("This device has been revoked by an administrator.", ApiRoutes.Problems.DeviceRevoked);
    }

    private Task<User?> LoadUserAsync(System.Linq.Expressions.Expression<Func<User, bool>> predicate, CancellationToken ct) =>
        db.Users
            .Include(u => u.Branches)
            .Include(u => u.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(predicate, ct);

    private async Task<List<BranchSummaryDto>> BranchSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        return await db.Branches
            .Where(b => ids.Contains(b.Id))
            .OrderBy(b => b.Code)
            .Select(b => new BranchSummaryDto(b.Id, b.Code, b.Name))
            .ToListAsync(ct);
    }

    private async Task<List<string>> RoleNamesAsync(Guid userId, CancellationToken ct) =>
        await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.Role!.Name).ToListAsync(ct);

    /// <summary>Union of role grants; when two roles grant the same permission the wider scope wins.</summary>
    public static IReadOnlyList<GrantedPermission> ResolvePermissions(User user) =>
        user.Roles
            .Where(r => r.Role is { IsDeleted: false })
            .SelectMany(r => r.Role!.Permissions)
            .GroupBy(p => p.Permission)
            .Select(g => new GrantedPermission(g.Key, g.Max(p => p.Scope)))
            .OrderBy(p => p.Name)
            .ToList();

    private static TokenSubject UserSubject(User user, Tenant tenant, Guid? deviceId) => new(
        user.Id, user.UserName, user.FullName, tenant.Id, tenant.Code, false,
        user.Branches.Select(b => b.BranchId).ToList(), ResolvePermissions(user), deviceId,
        user.Roles.Where(r => r.Role is { IsDeleted: false }).Select(r => r.Role!.Name).ToList());

    private static TokenSubject AdminSubject(PlatformAdmin admin) => new(
        admin.Id, admin.UserName, admin.FullName, null, null, true, [], DefaultRoles.SuperAdminPermissions, null, ["Super Admin"]);

    private static MeDto BuildMe(TokenSubject s, Tenant? tenant, IReadOnlyList<BranchSummaryDto> branches, IReadOnlyList<string> roles) => new(
        s.UserId, s.UserName, s.FullName, s.TenantId, tenant?.Code, tenant?.Name, s.IsSuperAdmin,
        branches, roles, s.Permissions, tenant?.OfflineGraceDays ?? 0);

    private static bool IsLocked(DateTime? lockoutEnd, DateTime now) => lockoutEnd is { } end && end > now;

    private void BurnHash(string password)
    {
        _dummyHash ??= hasher.Hash(Guid.NewGuid().ToString());
        hasher.Verify(_dummyHash, password);
    }
}

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).StrongPassword().NotEqual(x => x.CurrentPassword)
            .WithMessage("New password must be different from the current one.");
    }
}
