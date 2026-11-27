using Clinic.Api.Security;
using Clinic.Application.Abstractions;
using Clinic.Application.Clinical;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>FR-SYN-32: duplicate suggestions and admin-confirmed merge.</summary>
[ApiController]
public sealed class PatientMergeController(PatientMergeService merges) : ControllerBase
{
    [HttpGet(HardeningRoutes.Duplicates), HasPermission(Permissions.Patients.Merge)]
    public Task<IReadOnlyList<DuplicatePairDto>> Duplicates(Guid? branchId, CancellationToken ct) => merges.SuggestAsync(branchId, ct);

    [HttpPost(HardeningRoutes.Merge), HasPermission(Permissions.Patients.Merge)]
    public Task<MergeResultDto> Merge(MergePatientsRequest r, CancellationToken ct) => merges.MergeAsync(r, ct);
}

/// <summary>FR-SEC-07 audit viewer, and crash / sync-error reports from the app.</summary>
[ApiController]
public sealed class AuditController(AuditService audit, ICurrentUser user) : ControllerBase
{
    [HttpGet(HardeningRoutes.Audit), HasPermission(Permissions.Audit.View)]
    public Task<PagedResult<AuditEntryDto>> Search(string? entityType, string? entityId, Guid? userId, string? action, DateTime? from, DateTime? to,
        int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        audit.SearchAsync(entityType, entityId, userId, action, from, to, page, pageSize, ct);

    [HttpPost(HardeningRoutes.ClientErrors), Authorize]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> ClientError(ClientErrorReport report, CancellationToken ct)
    {
        await audit.RecordClientErrorAsync(report, user, ct);
        return Accepted();
    }
}
