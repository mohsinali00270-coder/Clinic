using Clinic.Api.Security;
using Clinic.Application.Sync;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>§7 sync endpoints. Push bodies may be gzip/Brotli compressed (Content-Encoding).</summary>
[ApiController]
[Authorize]
[Route(SyncRoutes.Root)]
public sealed class SyncController(SyncService sync) : ControllerBase
{
    [HttpPost("push")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public Task<SyncPushResponse> Push(SyncPushRequest request, CancellationToken ct) => sync.PushAsync(request, ct);

    [HttpGet("pull")]
    public Task<SyncPullResponse> Pull(Guid deviceId, string appVersion, long since = 0, int pageSize = 500, CancellationToken ct = default) =>
        sync.PullAsync(deviceId, appVersion, since, pageSize, ct);

    /// <summary>FR-SYN-23: initial snapshot for a new (or reset) device.</summary>
    [HttpGet("snapshot")]
    public async Task Snapshot(Guid deviceId, string appVersion, CancellationToken ct)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers["X-Content-Encoding"] = "gzip";
        await sync.WriteSnapshotAsync(deviceId, appVersion, Response.Body, ct);
    }

    [HttpGet("status")]
    public Task<SyncStatusResponse> Status(CancellationToken ct) => sync.StatusAsync(ct);

    /// <summary>FR-SYN-30: conflicts settled on the device (after the resolving change was pushed).</summary>
    [HttpPost("conflicts/resolutions")]
    public async Task<IActionResult> Resolve(IReadOnlyList<ConflictResolutionReport> reports, CancellationToken ct)
    {
        await sync.ResolveConflictsAsync(reports, ct);
        return NoContent();
    }

    /// <summary>Open (or all) conflicts of the clinic, for auditors.</summary>
    [HttpGet("conflicts"), HasPermission(Permissions.Audit.View)]
    public Task<IReadOnlyList<ConflictDto>> Conflicts(string? status, CancellationToken ct) => sync.ConflictsAsync(status, ct);
}

[ApiController]
[Route(ApiRoutes.Devices)]
public sealed class NumberRangesController(NumberRangeService ranges) : ControllerBase
{
    /// <summary>FR-OFF-22: lease a block of numbers for offline use (the device itself, or a device admin).</summary>
    [HttpPost("{id:guid}/number-ranges"), Authorize]
    public Task<NumberRangeDto> Lease(Guid id, LeaseNumberRangeRequest request, CancellationToken ct) =>
        ranges.LeaseAsync(id, request, ct);
}
