using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Application.Identity;
using Clinic.Application.Sync;
using Clinic.Application.Tenancy;
using Clinic.Domain.Identity;
using Clinic.Domain.Sync;
using Clinic.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Clinic.Infrastructure.Persistence;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    public bool Enabled { get; set; }
    public string SuperAdminUserName { get; set; } = "superadmin";
    public string SuperAdminPassword { get; set; } = "";
    public bool DemoTenant { get; set; }
    public string DemoTenantCode { get; set; } = "demo";
    public string DemoAdminPassword { get; set; } = "";
}

public static class DbInitializer
{
    /// <summary>Applies migrations (SQL Server) or creates the schema (SQLite tests), then seeds.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ClinicDbContext>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");
        var seed = sp.GetRequiredService<IConfiguration>().GetSection(SeedOptions.Section).Get<SeedOptions>() ?? new();

        if (db.Database.IsSqlServer())
            await db.Database.MigrateAsync(ct);
        else
            await db.Database.EnsureCreatedAsync(ct);

        if (seed.Enabled) await SeedAsync(db, sp, seed, logger, ct);
        await UpgradeTenantsAsync(db, logger, ct);
    }

    private static async Task SeedAsync(ClinicDbContext db, IServiceProvider sp, SeedOptions seed, ILogger logger, CancellationToken ct)
    {
        var hasher = sp.GetRequiredService<IPasswordHasher>();

        if (!await db.PlatformAdmins.AnyAsync(ct) && !string.IsNullOrEmpty(seed.SuperAdminPassword))
        {
            db.PlatformAdmins.Add(new PlatformAdmin
            {
                UserName = seed.SuperAdminUserName,
                NormalizedUserName = Normalize(seed.SuperAdminUserName),
                FullName = "Platform Super Admin",
                PasswordHash = hasher.Hash(seed.SuperAdminPassword),
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded Super Admin '{UserName}'.", seed.SuperAdminUserName);
        }

        if (seed.DemoTenant && !string.IsNullOrEmpty(seed.DemoAdminPassword) &&
            !await db.Tenants.AnyAsync(t => t.Code == seed.DemoTenantCode, ct))
        {
            await TenantProvisioner.ProvisionAsync(db, hasher, new CreateTenantRequest(
                seed.DemoTenantCode, "Demo Clinic", "MAIN", "Main Branch", "admin", "Demo Tenant Admin", seed.DemoAdminPassword), ct);
            logger.LogInformation("Seeded demo tenant '{Code}' with user 'admin'.", seed.DemoTenantCode);
        }
    }

    /// <summary>
    /// Idempotent per-tenant upgrades that run on every start:
    /// 1. built-in roles receive permissions introduced after the tenant was created;
    /// 2. sync entities that predate sync (RowVersion 0) get a ChangeLog entry so devices pull them.
    /// </summary>
    private static async Task UpgradeTenantsAsync(ClinicDbContext db, ILogger logger, CancellationToken ct)
    {
        var tenants = await db.Tenants.IgnoreQueryFilters().ToListAsync(ct);
        foreach (var tenant in tenants)
        {
            using var scope = db.BeginTenantScope(tenant.Id);

            if (tenant.PermissionCatalogVersion < DefaultRoles.CatalogVersion)
            {
                var roles = await db.Roles.Include(r => r.Permissions).Where(r => r.IsSystem).ToListAsync(ct);
                foreach (var role in roles)
                {
                    var definition = DefaultRoles.All.FirstOrDefault(d => d.Name == role.Name);
                    if (definition is null) continue;
                    foreach (var p in definition.Permissions.Where(p => DefaultRoles.VersionOf(role.Name, p.Name) > tenant.PermissionCatalogVersion))
                    {
                        if (role.Permissions.All(x => x.Permission != p.Name))
                            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p.Name, Scope = p.Scope });
                    }
                }
                tenant.PermissionCatalogVersion = DefaultRoles.CatalogVersion;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Upgraded built-in role permissions for tenant '{Code}'.", tenant.Code);
            }

            await Application.Forms.LookupService.EnsureDefaultsAsync(db, ct);
            await Application.Clinical.SchedulingSetupService.EnsureDefaultsAsync(db, ct);
            await Application.Clinical.ClinicalMasterService.EnsureDefaultsAsync(db, ct);
            await Application.Clinical.CareSetupService.EnsureDefaultsAsync(db, ct);
            await Application.Clinical.LabBillingService.EnsureDefaultsAsync(db, ct);
            await Application.Clinical.AccountsService.EnsureDefaultsAsync(db, ct);
            await Application.Clinical.InventoryReportService.EnsureDefaultsAsync(db, ct);

            foreach (var descriptor in SyncRegistry.All)
            {
                var touched = await (Task<int>)BackfillMethod.MakeGenericMethod(descriptor.ClrType).Invoke(null, [db, ct])!;
                if (touched > 0)
                    logger.LogInformation("Assigned sync versions to {Count} {Type} rows in tenant '{Code}'.", touched, descriptor.Name, tenant.Code);
            }
            db.ChangeTracker.Clear();
        }
    }

    private static readonly System.Reflection.MethodInfo BackfillMethod =
        typeof(DbInitializer).GetMethod(nameof(BackfillAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static async Task<int> BackfillAsync<T>(ClinicDbContext db, CancellationToken ct) where T : class, ISyncEntity
    {
        var tenant = db.CurrentTenantId;
        var rows = await db.Set<T>().IgnoreQueryFilters().Where(e => e.TenantId == tenant && e.RowVersion == 0).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        foreach (var row in rows) db.Entry(row).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
