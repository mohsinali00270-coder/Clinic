using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Identity;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Identity;

public sealed class RoleService(IAppDbContext db, ICurrentUser user, IValidator<SaveRoleRequest> validator)
{
    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct)
    {
        var roles = await db.Roles.Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(ToDto).ToList();
    }

    public async Task<RoleDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(request, ct);
        await EnsureNameFreeAsync(request.Name, null, ct);
        EnsureCallerCanGrant(request.Permissions);

        var role = new Role { Id = Guid.CreateVersion7(), Name = request.Name.Trim(), Description = Normalize.Optional(request.Description) };
        SetPermissions(role, request.Permissions);
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return ToDto(role);
    }

    public async Task<RoleDto> UpdateAsync(Guid id, SaveRoleRequest request, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(request, ct);
        var role = await LoadAsync(id, ct);

        if (role.IsSystem && role.Name == DefaultRoles.TenantAdmin)
            throw new ConflictException("The Tenant Admin role always holds every permission and cannot be edited.");

        await EnsureNameFreeAsync(request.Name, id, ct);
        EnsureCallerCanGrant(request.Permissions);

        if (!role.IsSystem) role.Name = request.Name.Trim();
        role.Description = Normalize.Optional(request.Description);

        db.RolePermissions.RemoveRange(role.Permissions);
        role.Permissions.Clear();
        SetPermissions(role, request.Permissions);
        await db.SaveChangesAsync(ct);
        return ToDto(role);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var role = await LoadAsync(id, ct);
        if (role.IsSystem) throw new ConflictException("Built-in roles cannot be deleted.");
        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == id, ct))
            throw new ConflictException("This role is still assigned to users.");

        role.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task<Role> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new NotFoundException("Role", id);

    private async Task EnsureNameFreeAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (await db.Roles.AnyAsync(r => r.Name == trimmed && r.Id != exceptId, ct))
            throw new RequestValidationException("name", "Another role already has this name.");
    }

    /// <summary>No privilege escalation: a caller can only grant what they hold, at no wider scope.</summary>
    private void EnsureCallerCanGrant(IEnumerable<GrantedPermission> permissions)
    {
        foreach (var p in permissions)
        {
            var mine = user.GetScope(p.Name);
            if (mine is null || mine < p.Scope)
                throw new ForbiddenException($"You cannot grant '{p.Name}' at scope {p.Scope} because you do not hold it.");
        }
    }

    private static void SetPermissions(Role role, IEnumerable<GrantedPermission> permissions)
    {
        foreach (var p in permissions.GroupBy(p => p.Name).Select(g => g.MaxBy(x => x.Scope)!))
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p.Name, Scope = p.Scope });
    }

    private static RoleDto ToDto(Role r) => new(
        r.Id, r.Name, r.Description, r.IsSystem,
        r.Permissions.OrderBy(p => p.Permission).Select(p => new GrantedPermission(p.Permission, p.Scope)).ToList());
}

public sealed class SaveRoleValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Permissions).NotNull();
        RuleForEach(x => x.Permissions).ChildRules(p =>
        {
            p.RuleFor(x => x.Name)
                .Must(n => Permissions.TenantAssignable.Contains(n))
                .WithMessage(x => $"'{x.Name}' is not a permission that can be assigned to a tenant role.");
            p.RuleFor(x => x.Scope).IsInEnum();
        });
    }
}
