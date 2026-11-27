using Clinic.Application.Identity;
using Clinic.Infrastructure.Persistence;
using Clinic.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Api.Tests;

/// <summary>Built-in roles of an existing clinic receive what later catalogue versions grant them.</summary>
public sealed class RoleUpgradeTests
{
    [Fact]
    public async Task An_older_permission_newly_granted_to_a_role_reaches_existing_clinics()
    {
        await using var factory = new ClinicApiFactory();
        _ = factory.CreateClient(); // starts the host: schema + seed at the current catalogue version

        // Simulate a clinic created at catalogue v5: the pharmacist lacks the v6 grants, and an admin removed one permission.
        Guid pharmacistRole;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Code == "demo");
            using var _ = db.BeginTenantScope(tenant.Id);
            var role = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.Name == "Pharmacist");
            pharmacistRole = role.Id;
            role.Permissions.RemoveAll(p => p.Permission is Permissions.Encounters.View or Permissions.Billing.Collect or Permissions.Notes.View);
            tenant.PermissionCatalogVersion = 5;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            await DbInitializer.InitializeAsync(scope.ServiceProvider);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            var granted = await db.RolePermissions.Where(p => p.RoleId == pharmacistRole).Select(p => p.Permission).ToListAsync();
            Assert.Contains(Permissions.Encounters.View, granted);   // granted to pharmacists in v6
            Assert.Contains(Permissions.Billing.Collect, granted);   // granted to pharmacists in v6
            Assert.DoesNotContain(Permissions.Notes.View, granted);  // an admin's earlier removal is respected
            Assert.Equal(DefaultRoles.CatalogVersion, (await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Code == "demo")).PermissionCatalogVersion);
        }
    }
}
