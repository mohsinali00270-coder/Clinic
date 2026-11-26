using System.Net;
using System.Net.Http.Json;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Tests;

public sealed class FoundationTests(ClinicApiFactory factory) : IClassFixture<ClinicApiFactory>
{
    [Fact]
    public async Task Health_endpoint_is_up()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Demo_admin_logs_in_with_all_tenant_permissions()
    {
        var token = await factory.LoginAsync("demo", "admin", ClinicApiFactory.DemoAdminPassword);

        Assert.False(string.IsNullOrEmpty(token.AccessToken));
        Assert.Equal("demo", token.Me.TenantCode);
        Assert.Contains("Tenant Admin", token.Me.Roles);
        Assert.Equal(Permissions.TenantAssignable.Count(), token.Me.Permissions.Count);
        Assert.All(token.Me.Permissions, p => Assert.Equal(PermissionScope.All, p.Scope));
        Assert.DoesNotContain(token.Me.Permissions, p => p.Name == Permissions.Tenants.Manage);
        Assert.Single(token.Me.Branches);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_tenant_are_rejected_with_problem_details()
    {
        using var client = factory.CreateClient();
        var bad = await client.PostAsJsonAsync(ApiRoutes.Auth.Login, new LoginRequest("demo", "admin", "nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);

        var unknown = await client.PostAsJsonAsync(ApiRoutes.Auth.Login, new LoginRequest("no-such-clinic", "admin", ClinicApiFactory.DemoAdminPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Admin_creates_a_branch_and_a_user_who_can_then_log_in()
    {
        using var admin = await factory.DemoAdminAsync();
        var code = HttpExtensions.Unique("B").ToUpperInvariant()[..8];

        var branch = await (await admin.PostAsJsonAsync(ApiRoutes.Branches,
            new SaveBranchRequest(code, "North Branch", "1 North Rd", "0300-0000000"))).ReadAsync<BranchDto>();
        Assert.Equal(code, branch.Code);

        var roles = await admin.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles);
        var reception = roles!.Single(r => r.Name == "Receptionist");

        var userName = HttpExtensions.Unique("rec");
        var user = await (await admin.PostAsJsonAsync(ApiRoutes.Users, new CreateUserRequest(
            userName, "Front Desk", null, "0300-1111111", "Reception@1", [reception.Id], [branch.Id]))).ReadAsync<UserDto>();
        Assert.Equal([branch.Id], user.BranchIds);

        var token = await factory.LoginAsync("demo", userName, "Reception@1");
        Assert.Equal([branch.Id], token.Me.Branches.Select(b => b.Id));
        Assert.Contains(token.Me.Permissions, p => p is { Name: Permissions.Patients.Create, Scope: PermissionScope.OwnBranch });
    }

    [Fact]
    public async Task Duplicate_branch_code_returns_validation_problem_with_field_key()
    {
        using var admin = await factory.DemoAdminAsync();
        var response = await admin.PostAsJsonAsync(ApiRoutes.Branches, new SaveBranchRequest("main", "Dup", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.True(problem!.Errors.ContainsKey("code"));
    }

    [Fact]
    public async Task Permissions_are_enforced_per_endpoint()
    {
        using var admin = await factory.DemoAdminAsync();
        var roles = await admin.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles);
        var branches = await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches);
        var userName = HttpExtensions.Unique("rcp");
        await (await admin.PostAsJsonAsync(ApiRoutes.Users, new CreateUserRequest(
            userName, "Receptionist", null, null, "Reception@1",
            [roles!.Single(r => r.Name == "Receptionist").Id], [branches!.First().Id]))).EnsureSuccessAsync();

        using var receptionist = await factory.ClientForAsync("demo", userName, "Reception@1");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await receptionist.PostAsJsonAsync(ApiRoutes.Branches, new SaveBranchRequest("ZZ", "Nope", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionist.GetAsync(ApiRoutes.Users)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionist.GetAsync(ApiRoutes.Tenants)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await receptionist.GetAsync(ApiRoutes.Auth.Me)).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ApiRoutes.Branches)).StatusCode);

        // Tenant admins cannot reach Super Admin endpoints either.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(ApiRoutes.Tenants)).StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_revokes_the_family()
    {
        var first = await factory.LoginAsync("demo", "admin", ClinicApiFactory.DemoAdminPassword);
        using var client = factory.CreateClient();

        var second = await (await client.PostAsJsonAsync(ApiRoutes.Auth.Refresh, new RefreshRequest(first.RefreshToken))).ReadAsync<TokenResponse>();
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Replaying the rotated token is treated as theft...
        var replay = await client.PostAsJsonAsync(ApiRoutes.Auth.Refresh, new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ...and kills the newer token from the same login too.
        var afterReplay = await client.PostAsJsonAsync(ApiRoutes.Auth.Refresh, new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    public async Task Account_locks_after_five_failed_attempts()
    {
        using var admin = await factory.DemoAdminAsync();
        var roles = await admin.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles);
        var branches = await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches);
        var userName = HttpExtensions.Unique("lock");
        await (await admin.PostAsJsonAsync(ApiRoutes.Users, new CreateUserRequest(
            userName, "Lock Test", null, null, "Correct@123",
            [roles!.Single(r => r.Name == "Nurse / Assistant").Id], [branches!.First().Id]))).EnsureSuccessAsync();

        using var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync(ApiRoutes.Auth.Login, new LoginRequest("demo", userName, "Wrong@123"));

        var locked = await client.PostAsJsonAsync(ApiRoutes.Auth.Login, new LoginRequest("demo", userName, "Correct@123"));
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        var problem = await locked.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(ApiRoutes.Problems.LockedOut, problem!.Type);

        // An admin password reset clears the lockout.
        var user = (await admin.GetFromJsonAsync<PagedResult<UserDto>>($"{ApiRoutes.Users}?search={userName}"))!.Items.Single();
        Assert.True(user.IsLockedOut);
        await (await admin.PostAsJsonAsync($"{ApiRoutes.Users}/{user.Id}/reset-password", new ResetPasswordRequest("Fresh@1234"))).EnsureSuccessAsync();
        await factory.LoginAsync("demo", userName, "Fresh@1234");
    }

    [Fact]
    public async Task Device_registers_gets_a_code_and_revocation_blocks_it()
    {
        var deviceId = Guid.NewGuid();
        var token = await factory.LoginAsync("demo", "admin", ClinicApiFactory.DemoAdminPassword, deviceId);
        using var device = factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);

        var registered = await (await device.PostAsJsonAsync($"{ApiRoutes.Devices}/register",
            new RegisterDeviceRequest(deviceId, "Front desk tablet", "Android", "Galaxy Tab", "1.0.0", token.Me.Branches[0].Id))).ReadAsync<DeviceDto>();
        Assert.StartsWith("D", registered.DeviceCode);
        Assert.False(registered.IsRevoked);

        // Registering again is idempotent and keeps the code.
        var again = await (await device.PostAsJsonAsync($"{ApiRoutes.Devices}/register",
            new RegisterDeviceRequest(deviceId, "Front desk tablet", "Android", "Galaxy Tab", "1.0.1", null))).ReadAsync<DeviceDto>();
        Assert.Equal(registered.DeviceCode, again.DeviceCode);

        using var admin = await factory.DemoAdminAsync();
        await (await admin.PostAsync($"{ApiRoutes.Devices}/{deviceId}/revoke", null)).EnsureSuccessAsync();

        var refresh = await factory.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Refresh, new RefreshRequest(token.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        var login = await factory.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("demo", "admin", ClinicApiFactory.DemoAdminPassword, deviceId));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(ApiRoutes.Problems.DeviceRevoked, (await login.Content.ReadFromJsonAsync<ProblemDetails>())!.Type);
    }

    [Fact]
    public async Task Roles_cannot_grant_unknown_or_platform_permissions()
    {
        using var admin = await factory.DemoAdminAsync();
        var bad = await admin.PostAsJsonAsync(ApiRoutes.Roles, new SaveRoleRequest(HttpExtensions.Unique("r"), null,
            [new GrantedPermission(Permissions.Tenants.Manage, PermissionScope.All)]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await admin.PostAsJsonAsync(ApiRoutes.Roles, new SaveRoleRequest(HttpExtensions.Unique("r"), "Billing clerk",
            [new GrantedPermission(Permissions.Billing.View, PermissionScope.OwnBranch)]));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }
}
