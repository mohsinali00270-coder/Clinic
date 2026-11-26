using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Sync;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Sync;

/// <summary>
/// FR-OFF-22: devices lease blocks of MRN, token and invoice numbers so they can number records offline
/// without collisions. Blocks never overlap because the per-tenant counter row is locked while reserving.
/// </summary>
public sealed class NumberRangeService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const int MinSize = 10;
    public const int MaxSize = 5000;

    public async Task<NumberRangeDto> LeaseAsync(Guid deviceId, LeaseNumberRangeRequest request, CancellationToken ct)
    {
        if (!NumberRangeKinds.All.Contains(request.Kind))
            throw new RequestValidationException("kind", $"Kind must be one of: {string.Join(", ", NumberRangeKinds.All)}.");
        if (request.Size is < MinSize or > MaxSize)
            throw new RequestValidationException("size", $"Size must be between {MinSize} and {MaxSize}.");

        var ownDevice = user.DeviceId == deviceId;
        if (!ownDevice && user.GetScope(Permissions.Devices.Manage) is null)
            throw new ForbiddenException("Number ranges can only be leased by the device itself or a device administrator.");

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device", deviceId);
        if (device.IsRevoked) throw new ConflictException("This device has been revoked.");

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var start = await db.ReserveNumbersAsync(request.Kind, request.Size, ct);

            var range = new NumberRange
            {
                Id = Guid.CreateVersion7(),
                DeviceId = deviceId,
                Kind = request.Kind,
                StartValue = start,
                EndValue = start + request.Size - 1,
                LeasedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.NumberRanges.Add(range);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new NumberRangeDto(range.Id, range.Kind, range.StartValue, range.EndValue, range.LeasedAt);
        });
    }
}
