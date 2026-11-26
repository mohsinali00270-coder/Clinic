using System.Security.Claims;
using Clinic.Application.Abstractions;
using Clinic.Shared.Security;

namespace Clinic.Api.Security;

/// <summary>Reads the caller from the validated JWT. Parsed once per request.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private Parsed? _parsed;

    private Parsed P => _parsed ??= Parse(accessor.HttpContext?.User);

    public bool IsAuthenticated => P.IsAuthenticated;
    public Guid? UserId => P.UserId;
    public Guid? TenantId => P.TenantId;
    public bool IsSuperAdmin => P.IsSuperAdmin;
    public Guid? DeviceId => P.DeviceId;
    public IReadOnlyList<Guid> BranchIds => P.BranchIds;
    public IReadOnlyList<string> Roles => P.Roles;

    public PermissionScope? GetScope(string permission) =>
        P.Permissions.TryGetValue(permission, out var scope) ? scope : null;

    private sealed record Parsed(
        bool IsAuthenticated, Guid? UserId, Guid? TenantId, bool IsSuperAdmin, Guid? DeviceId,
        IReadOnlyList<Guid> BranchIds, IReadOnlyDictionary<string, PermissionScope> Permissions, IReadOnlyList<string> Roles);

    private static Parsed Parse(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return new(false, null, null, false, null, [], new Dictionary<string, PermissionScope>(), []);

        static Guid? G(string? v) => Guid.TryParse(v, out var g) ? g : null;

        var permissions = new Dictionary<string, PermissionScope>();
        foreach (var claim in principal.FindAll(ClinicClaimTypes.Permission))
        {
            var sep = claim.Value.LastIndexOf(':');
            if (sep <= 0 || !Enum.TryParse<PermissionScope>(claim.Value[(sep + 1)..], out var scope)) continue;
            var name = claim.Value[..sep];
            permissions[name] = permissions.TryGetValue(name, out var existing) && existing > scope ? existing : scope;
        }

        return new(
            true,
            G(principal.FindFirstValue(ClinicClaimTypes.UserId)),
            G(principal.FindFirstValue(ClinicClaimTypes.TenantId)),
            principal.FindFirstValue(ClinicClaimTypes.SuperAdmin) == "true",
            G(principal.FindFirstValue(ClinicClaimTypes.DeviceId)),
            principal.FindAll(ClinicClaimTypes.Branch).Select(c => G(c.Value)).OfType<Guid>().ToList(),
            permissions,
            principal.FindAll(ClinicClaimTypes.Role).Select(c => c.Value).ToList());
    }
}
