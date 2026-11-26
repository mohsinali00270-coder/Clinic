using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Identity;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Identity;

public sealed class UserService(
    IAppDbContext db,
    ICurrentUser current,
    IPasswordHasher hasher,
    TimeProvider clock,
    IValidator<CreateUserRequest> createValidator,
    IValidator<UpdateUserRequest> updateValidator,
    IValidator<ResetPasswordRequest> resetValidator)
{
    public async Task<PagedResult<UserDto>> ListAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = Visible(Permissions.Users.View);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(u => u.UserName.Contains(s) || u.FullName.Contains(s) || (u.Phone != null && u.Phone.Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var users = await query
            .Include(u => u.Roles).Include(u => u.Branches)
            .OrderBy(u => u.FullName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        return new PagedResult<UserDto>(users.Select(u => ToDto(u, now)).ToList(), page, pageSize, total);
    }

    public async Task<UserDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await LoadAsync(Permissions.Users.View, id, ct), clock.GetUtcNow().UtcDateTime);

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        await createValidator.ValidateOrThrowAsync(request, ct);

        var normalized = Normalize.Key(request.UserName);
        if (await db.Users.AnyAsync(u => u.NormalizedUserName == normalized, ct))
            throw new RequestValidationException("userName", "This user name is already taken.");

        await EnsureAssignableAsync(request.RoleIds, request.BranchIds, ct);

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            UserName = request.UserName.Trim(),
            NormalizedUserName = normalized,
            FullName = request.FullName.Trim(),
            Email = Normalize.Optional(request.Email),
            Phone = Normalize.Optional(request.Phone),
            PasswordHash = hasher.Hash(request.Password),
        };
        SetAssignments(user, request.RoleIds, request.BranchIds);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return ToDto(user, clock.GetUtcNow().UtcDateTime);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        await updateValidator.ValidateOrThrowAsync(request, ct);
        var user = await LoadAsync(Permissions.Users.Manage, id, ct);

        if (id == current.UserId && !request.IsActive)
            throw new RequestValidationException("isActive", "You cannot deactivate your own account.");

        await EnsureAssignableAsync(request.RoleIds, request.BranchIds, ct);

        user.FullName = request.FullName.Trim();
        user.Email = Normalize.Optional(request.Email);
        user.Phone = Normalize.Optional(request.Phone);
        user.IsActive = request.IsActive;

        db.UserRoles.RemoveRange(user.Roles);
        db.UserBranches.RemoveRange(user.Branches);
        user.Roles.Clear();
        user.Branches.Clear();
        SetAssignments(user, request.RoleIds, request.BranchIds);

        if (!user.IsActive) await RevokeSessionsAsync(user.Id, ct);

        await db.SaveChangesAsync(ct);
        return ToDto(user, clock.GetUtcNow().UtcDateTime);
    }

    public async Task ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        await resetValidator.ValidateOrThrowAsync(request, ct);
        var user = await LoadAsync(Permissions.Users.ResetPassword, id, ct);
        user.PasswordHash = hasher.Hash(request.NewPassword);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        await RevokeSessionsAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (id == current.UserId) throw new ConflictException("You cannot delete your own account.");
        var user = await LoadAsync(Permissions.Users.Manage, id, ct);
        user.IsDeleted = true;
        user.IsActive = false;
        await RevokeSessionsAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// FR-AUTH-02 scopes: All = every user in the tenant; OwnBranch = users sharing a branch with the caller;
    /// OwnRecords = only the caller.
    /// </summary>
    private IQueryable<User> Visible(string permission)
    {
        var scope = current.GetScope(permission);
        var me = current.UserId;
        var myBranches = current.BranchIds;
        return scope switch
        {
            PermissionScope.All => db.Users,
            PermissionScope.OwnBranch => db.Users.Where(u => u.Id == me || u.Branches.Any(b => myBranches.Contains(b.BranchId))),
            _ => db.Users.Where(u => u.Id == me),
        };
    }

    private async Task<User> LoadAsync(string permission, Guid id, CancellationToken ct) =>
        await Visible(permission)
            .Include(u => u.Roles).Include(u => u.Branches)
            .AsSplitQuery()
            .FirstOrDefaultAsync(u => u.Id == id, ct)
        ?? throw new NotFoundException("User", id);

    /// <summary>
    /// Role and branch ids are looked up through tenant-filtered sets, so ids from another tenant
    /// simply do not resolve. The caller also cannot hand out more than they hold.
    /// </summary>
    private async Task EnsureAssignableAsync(IReadOnlyList<Guid> roleIds, IReadOnlyList<Guid> branchIds, CancellationToken ct)
    {
        var roles = await db.Roles.Include(r => r.Permissions).Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        if (roles.Count != roleIds.Distinct().Count())
            throw new RequestValidationException("roleIds", "One or more roles do not exist.");

        var branchCount = await db.Branches.CountAsync(b => branchIds.Contains(b.Id), ct);
        if (branchCount != branchIds.Distinct().Count())
            throw new RequestValidationException("branchIds", "One or more branches do not exist.");

        foreach (var grant in roles.SelectMany(r => r.Permissions))
        {
            var mine = current.GetScope(grant.Permission);
            if (mine is null || mine < grant.Scope)
                throw new ForbiddenException($"You cannot assign a role that grants '{grant.Permission}' because you do not hold it.");
        }

        if (current.GetScope(Permissions.Users.Manage) != PermissionScope.All && branchIds.Except(current.BranchIds).Any())
            throw new ForbiddenException("You can only assign users to your own branches.");
    }

    private static void SetAssignments(User user, IEnumerable<Guid> roleIds, IEnumerable<Guid> branchIds)
    {
        user.Roles.AddRange(roleIds.Distinct().Select(r => new UserRole { UserId = user.Id, RoleId = r }));
        user.Branches.AddRange(branchIds.Distinct().Select(b => new UserBranch { UserId = user.Id, BranchId = b }));
    }

    private Task RevokeSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.RefreshTokens
            .Where(t => t.SubjectId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static UserDto ToDto(User u, DateTime now) => new(
        u.Id, u.UserName, u.FullName, u.Email, u.Phone, u.IsActive, u.IsLockedOut(now),
        u.Roles.Select(r => r.RoleId).ToList(), u.Branches.Select(b => b.BranchId).ToList());
}

public sealed class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(100).Matches(@"^[A-Za-z0-9._@-]+$")
            .WithMessage("Use letters, digits and . _ @ - only.");
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage("Pick at least one role.");
        RuleFor(x => x.BranchIds).NotEmpty().WithMessage("Pick at least one branch.");
    }
}

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage("Pick at least one role.");
        RuleFor(x => x.BranchIds).NotEmpty().WithMessage("Pick at least one branch.");
    }
}

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator() => RuleFor(x => x.NewPassword).StrongPassword();
}
