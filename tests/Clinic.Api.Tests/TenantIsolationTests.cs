using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Clinic.Domain.Common;
using Clinic.Domain.Tenancy;
using Clinic.Infrastructure.Persistence;
using Clinic.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Api.Tests;

/// <summary>FR-SEC-08: no query may return another tenant's rows.</summary>
public sealed class TenantIsolationTests(ClinicApiFactory factory) : IClassFixture<ClinicApiFactory>
{
    private async Task<(HttpClient Client, string Code)> CreateSecondTenantAsync()
    {
        using var super = await factory.SuperAdminAsync();
        var code = HttpExtensions.Unique("t").ToLowerInvariant();
        var created = await (await super.PostAsJsonAsync(ApiRoutes.Tenants, new CreateTenantRequest(
            code, "Second Clinic", "HQ", "Head Office", "owner", "Second Owner", "Owner@1234"))).ReadAsync<TenantDto>();
        Assert.Equal(code, created.Code);

        return (await factory.ClientForAsync(code, "owner", "Owner@1234"), code);
    }

    [Fact]
    public async Task Second_tenant_admin_sees_only_their_own_branches_users_and_roles()
    {
        using var demo = await factory.DemoAdminAsync();
        var demoBranches = await demo.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches);
        var demoUsers = await demo.GetFromJsonAsync<PagedResult<UserDto>>(ApiRoutes.Users);
        var demoRoles = await demo.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles);

        var (other, _) = await CreateSecondTenantAsync();
        using (other)
        {
            var branches = await other.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches);
            var users = await other.GetFromJsonAsync<PagedResult<UserDto>>(ApiRoutes.Users);
            var roles = await other.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles);

            Assert.Equal(["HQ"], branches!.Select(b => b.Code));
            Assert.Equal(["owner"], users!.Items.Select(u => u.UserName));
            Assert.Empty(branches.Select(b => b.Id).Intersect(demoBranches!.Select(b => b.Id)));
            Assert.Empty(users.Items.Select(u => u.Id).Intersect(demoUsers!.Items.Select(u => u.Id)));
            Assert.Empty(roles!.Select(r => r.Id).Intersect(demoRoles!.Select(r => r.Id)));
        }
    }

    [Fact]
    public async Task Another_tenants_records_are_not_found_by_id_and_cannot_be_changed()
    {
        using var demo = await factory.DemoAdminAsync();
        var demoBranch = (await demo.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.First();
        var demoUser = (await demo.GetFromJsonAsync<PagedResult<UserDto>>(ApiRoutes.Users))!.Items.First();
        var demoRole = (await demo.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles))!.First();

        var (other, _) = await CreateSecondTenantAsync();
        using (other)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ApiRoutes.Branches}/{demoBranch.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ApiRoutes.Users}/{demoUser.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ApiRoutes.Roles}/{demoRole.Id}")).StatusCode);

            var update = await other.PutAsJsonAsync($"{ApiRoutes.Branches}/{demoBranch.Id}",
                new SaveBranchRequest("HACK", "Hacked", null, null));
            Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

            var delete = await other.DeleteAsync($"{ApiRoutes.Users}/{demoUser.Id}");
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

            // Ids from another tenant do not resolve when assigning roles/branches.
            var create = await other.PostAsJsonAsync(ApiRoutes.Users, new CreateUserRequest(
                "intruder", "Intruder", null, null, "Intruder@123", [demoRole.Id], [demoBranch.Id]));
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        }

        var stillThere = await demo.GetFromJsonAsync<BranchDto>($"{ApiRoutes.Branches}/{demoBranch.Id}");
        Assert.Equal(demoBranch.Name, stillThere!.Name);
    }

    [Fact]
    public async Task Every_tenant_table_is_filtered_by_the_current_tenant()
    {
        var (other, code) = await CreateSecondTenantAsync();
        other.Dispose();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var tenantId = await db.Tenants.Where(t => t.Code == code).Select(t => t.Id).SingleAsync();

        var tenantEntityTypes = db.Model.GetEntityTypes()
            .Select(e => e.ClrType)
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(tenantEntityTypes);

        var check = typeof(TenantIsolationTests).GetMethod(nameof(AssertOnlyTenantRowsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        using (db.BeginTenantScope(tenantId))
        {
            foreach (var type in tenantEntityTypes)
                await (Task)check.MakeGenericMethod(type).Invoke(null, [db, tenantId])!;
        }

        // With no tenant at all (anonymous / platform), tenant tables return nothing.
        using (db.BeginTenantScope(null))
        {
            Assert.Equal(0, await db.Branches.CountAsync());
            Assert.Equal(0, await db.Users.CountAsync());
            Assert.Equal(0, await db.Roles.CountAsync());
            Assert.Equal(0, await db.UserRoles.CountAsync());
        }
    }

    private static async Task AssertOnlyTenantRowsAsync<T>(ClinicDbContext db, Guid tenantId) where T : class, ITenantEntity
    {
        var visible = await db.Set<T>().Select(e => e.TenantId).Distinct().ToListAsync();
        Assert.All(visible, id => Assert.Equal(tenantId, id));

        var foreign = await db.Set<T>().IgnoreQueryFilters().CountAsync(e => e.TenantId != tenantId);
        var leaked = await db.Set<T>().CountAsync(e => e.TenantId != tenantId);
        Assert.Equal(0, leaked);
        _ = foreign; // other tenants' rows exist in the database but are invisible
    }

    [Fact]
    public async Task Writes_to_another_tenants_rows_are_refused_by_the_context()
    {
        var (other, code) = await CreateSecondTenantAsync();
        other.Dispose();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var otherId = await db.Tenants.Where(t => t.Code == code).Select(t => t.Id).SingleAsync();
        var demoId = await db.Tenants.Where(t => t.Code == "demo").Select(t => t.Id).SingleAsync();

        using (db.BeginTenantScope(otherId))
        {
            var demoBranch = await db.Branches.IgnoreQueryFilters().FirstAsync(b => b.TenantId == demoId);
            demoBranch.Name = "Hijacked";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.Branches.Add(new Branch { TenantId = demoId, Code = "X1", Name = "Planted" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
        }
    }
}
