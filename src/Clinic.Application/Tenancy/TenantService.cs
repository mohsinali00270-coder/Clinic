using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Application.Identity;
using Clinic.Domain.Identity;
using Clinic.Domain.Tenancy;
using Clinic.Shared.Contracts;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Tenancy;

/// <summary>Super Admin only: create and manage clinic organisations.</summary>
public sealed class TenantService(
    IAppDbContext db,
    IPasswordHasher hasher,
    IValidator<CreateTenantRequest> createValidator,
    IValidator<UpdateTenantRequest> updateValidator)
{
    public async Task<IReadOnlyList<TenantDto>> ListAsync(CancellationToken ct) =>
        await db.Tenants.OrderBy(t => t.Name).Select(t => ToDto(t)).ToListAsync(ct);

    public async Task<TenantDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id));

    public async Task<TenantDto> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        await createValidator.ValidateOrThrowAsync(request, ct);
        var code = request.Code.Trim().ToLowerInvariant();
        if (await db.Tenants.AnyAsync(t => t.Code == code, ct))
            throw new RequestValidationException("code", "A tenant with this code already exists.");

        var tenant = await TenantProvisioner.ProvisionAsync(db, hasher, request, ct);
        return ToDto(tenant);
    }

    public async Task<TenantDto> UpdateAsync(Guid id, UpdateTenantRequest request, CancellationToken ct)
    {
        await updateValidator.ValidateOrThrowAsync(request, ct);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        tenant.Name = request.Name.Trim();
        tenant.IsActive = request.IsActive;
        tenant.OfflineGraceDays = request.OfflineGraceDays;
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    private static TenantDto ToDto(Tenant t) => new(t.Id, t.Code, t.Name, t.IsActive, t.OfflineGraceDays, t.CreatedAt);
}

/// <summary>Creates a tenant with its first branch, the default roles and a Tenant Admin.</summary>
public static class TenantProvisioner
{
    public static async Task<Tenant> ProvisionAsync(IAppDbContext db, IPasswordHasher hasher, CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = request.Code.Trim().ToLowerInvariant(),
            Name = request.Name.Trim(),
            PermissionCatalogVersion = DefaultRoles.CatalogVersion,
        };
        db.Tenants.Add(tenant);

        using var scope = db.BeginTenantScope(tenant.Id);

        var branch = new Branch
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Code = Normalize.Key(request.FirstBranchCode),
            Name = request.FirstBranchName.Trim(),
        };
        db.Branches.Add(branch);

        Role? adminRole = null;
        foreach (var definition in DefaultRoles.All)
        {
            var role = new Role
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                Name = definition.Name,
                Description = definition.Description,
                IsSystem = true,
            };
            role.Permissions.AddRange(definition.Permissions.Select(p => new RolePermission { RoleId = role.Id, Permission = p.Name, Scope = p.Scope }));
            db.Roles.Add(role);
            if (definition.Name == DefaultRoles.TenantAdmin) adminRole = role;
        }

        var admin = new User
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            UserName = request.AdminUserName.Trim(),
            NormalizedUserName = Normalize.Key(request.AdminUserName),
            FullName = request.AdminFullName.Trim(),
            PasswordHash = hasher.Hash(request.AdminPassword),
        };
        admin.Roles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole!.Id });
        admin.Branches.Add(new UserBranch { UserId = admin.Id, BranchId = branch.Id });
        db.Users.Add(admin);

        await db.SaveChangesAsync(ct);
        await Forms.LookupService.EnsureDefaultsAsync(db, ct);
        await Clinical.SchedulingSetupService.EnsureDefaultsAsync(db, ct);
        await Clinical.ClinicalMasterService.EnsureDefaultsAsync(db, ct);
        await Clinical.CareSetupService.EnsureDefaultsAsync(db, ct);
        await Clinical.LabBillingService.EnsureDefaultsAsync(db, ct);
        await Clinical.AccountsService.EnsureDefaultsAsync(db, ct);
        await Clinical.InventoryReportService.EnsureDefaultsAsync(db, ct);
        return tenant;
    }
}

public sealed class CreateTenantValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.Code).Code(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.FirstBranchCode).Code(10);
        RuleFor(x => x.FirstBranchName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AdminUserName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.AdminFullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AdminPassword).StrongPassword();
    }
}

public sealed class UpdateTenantValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OfflineGraceDays).InclusiveBetween(1, 90);
    }
}
