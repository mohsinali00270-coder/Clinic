using Clinic.Api.Security;
using Clinic.Application.Devices;
using Clinic.Application.Identity;
using Clinic.Application.Tenancy;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Tenants)]
public sealed class TenantsController(TenantService tenants) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.Tenants.View)]
    public Task<IReadOnlyList<TenantDto>> List(CancellationToken ct) => tenants.ListAsync(ct);

    [HttpGet("{id:guid}"), HasPermission(Permissions.Tenants.View)]
    public Task<TenantDto> Get(Guid id, CancellationToken ct) => tenants.GetAsync(id, ct);

    [HttpPost, HasPermission(Permissions.Tenants.Manage)]
    public async Task<ActionResult<TenantDto>> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var created = await tenants.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), HasPermission(Permissions.Tenants.Manage)]
    public Task<TenantDto> Update(Guid id, UpdateTenantRequest request, CancellationToken ct) => tenants.UpdateAsync(id, request, ct);
}

[ApiController]
[Route(ApiRoutes.Branches)]
public sealed class BranchesController(BranchService branches) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.Branches.View)]
    public Task<IReadOnlyList<BranchDto>> List(CancellationToken ct) => branches.ListAsync(ct);

    [HttpGet("{id:guid}"), HasPermission(Permissions.Branches.View)]
    public Task<BranchDto> Get(Guid id, CancellationToken ct) => branches.GetAsync(id, ct);

    [HttpPost, HasPermission(Permissions.Branches.Manage)]
    public async Task<ActionResult<BranchDto>> Create(SaveBranchRequest request, CancellationToken ct)
    {
        var created = await branches.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), HasPermission(Permissions.Branches.Manage)]
    public Task<BranchDto> Update(Guid id, SaveBranchRequest request, CancellationToken ct) => branches.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}"), HasPermission(Permissions.Branches.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await branches.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route(ApiRoutes.Roles)]
public sealed class RolesController(RoleService roles) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.Roles.View)]
    public Task<IReadOnlyList<RoleDto>> List(CancellationToken ct) => roles.ListAsync(ct);

    [HttpGet("{id:guid}"), HasPermission(Permissions.Roles.View)]
    public Task<RoleDto> Get(Guid id, CancellationToken ct) => roles.GetAsync(id, ct);

    [HttpPost, HasPermission(Permissions.Roles.Manage)]
    public async Task<ActionResult<RoleDto>> Create(SaveRoleRequest request, CancellationToken ct)
    {
        var created = await roles.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), HasPermission(Permissions.Roles.Manage)]
    public Task<RoleDto> Update(Guid id, SaveRoleRequest request, CancellationToken ct) => roles.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}"), HasPermission(Permissions.Roles.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await roles.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route(ApiRoutes.Permissions)]
public sealed class PermissionsController : ControllerBase
{
    /// <summary>The catalogue of permissions a tenant role can hold.</summary>
    [HttpGet, HasPermission(Permissions.Roles.View)]
    public IEnumerable<string> List() => Permissions.TenantAssignable;
}

[ApiController]
[Route(ApiRoutes.Users)]
public sealed class UsersController(UserService users) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.Users.View)]
    public Task<PagedResult<UserDto>> List(string? search, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        users.ListAsync(search, page, pageSize, ct);

    [HttpGet("{id:guid}"), HasPermission(Permissions.Users.View)]
    public Task<UserDto> Get(Guid id, CancellationToken ct) => users.GetAsync(id, ct);

    [HttpPost, HasPermission(Permissions.Users.Manage)]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var created = await users.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), HasPermission(Permissions.Users.Manage)]
    public Task<UserDto> Update(Guid id, UpdateUserRequest request, CancellationToken ct) => users.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}"), HasPermission(Permissions.Users.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await users.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reset-password"), HasPermission(Permissions.Users.ResetPassword)]
    public async Task<IActionResult> ResetPassword(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        await users.ResetPasswordAsync(id, request, ct);
        return NoContent();
    }
}

[ApiController]
[Route(ApiRoutes.Devices)]
public sealed class DevicesController(DeviceService devices) : ControllerBase
{
    /// <summary>Any signed-in staff device registers itself on first login (FR-OFF-30).</summary>
    [HttpPost("register"), Authorize]
    public Task<DeviceDto> Register(RegisterDeviceRequest request, CancellationToken ct) => devices.RegisterAsync(request, ct);

    [HttpGet, HasPermission(Permissions.Devices.View)]
    public Task<IReadOnlyList<DeviceDto>> List(CancellationToken ct) => devices.ListAsync(ct);

    [HttpPut("{id:guid}/name"), HasPermission(Permissions.Devices.Manage)]
    public Task<DeviceDto> Rename(Guid id, RenameDeviceRequest request, CancellationToken ct) => devices.RenameAsync(id, request, ct);

    [HttpPost("{id:guid}/revoke"), HasPermission(Permissions.Devices.Manage)]
    public Task<DeviceDto> Revoke(Guid id, CancellationToken ct) => devices.RevokeAsync(id, ct);
}
