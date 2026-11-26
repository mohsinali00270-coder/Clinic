using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Devices;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Devices;

/// <summary>FR-OFF-30: device registration, listing, rename and revoke.</summary>
public sealed class DeviceService(
    IAppDbContext db,
    ICurrentUser user,
    TimeProvider clock,
    IValidator<RegisterDeviceRequest> registerValidator,
    IValidator<RenameDeviceRequest> renameValidator)
{
    public async Task<DeviceDto> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct)
    {
        await registerValidator.ValidateOrThrowAsync(request, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        if (request.BranchId is { } branchId)
        {
            var allowed = user.GetScope(Permissions.Branches.View) == PermissionScope.All || user.BranchIds.Contains(branchId);
            if (!allowed || !await db.Branches.AnyAsync(b => b.Id == branchId, ct))
                throw new RequestValidationException("branchId", "Branch not found or not assigned to you.");
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        if (device is null)
        {
            // GUIDs are globally unique, so a hit outside this tenant means the install moved clinics.
            var elsewhere = await db.Devices.IgnoreQueryFilters().AnyAsync(d => d.Id == request.DeviceId, ct);
            if (elsewhere)
                throw new ConflictException("This device is registered to another clinic. Reinstall the app to use it here.");

            var next = (await db.Devices.IgnoreQueryFilters()
                .Where(d => d.TenantId == db.CurrentTenantId)
                .MaxAsync(d => (int?)d.DeviceNumber, ct) ?? 0) + 1;

            device = new Device
            {
                Id = request.DeviceId,
                DeviceNumber = next,
                DeviceCode = ToDeviceCode(next),
                RegisteredAt = now,
                RegisteredByUserId = user.UserId,
            };
            db.Devices.Add(device);
        }
        else if (device.IsRevoked)
        {
            throw new AuthFailedException("This device has been revoked by an administrator.", ApiRoutes.Problems.DeviceRevoked);
        }

        if (string.IsNullOrWhiteSpace(device.Name)) device.Name = request.Name.Trim();
        device.Platform = request.Platform.Trim();
        device.Model = Normalize.Optional(request.Model);
        device.AppVersion = request.AppVersion.Trim();
        device.BranchId = request.BranchId ?? device.BranchId;
        device.LastSeenAt = now;

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(device, ct);
    }

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct)
    {
        var query = db.Devices.AsQueryable();
        if (user.GetScope(Permissions.Devices.View) != PermissionScope.All)
        {
            var mine = user.BranchIds;
            query = query.Where(d => d.BranchId != null && mine.Contains(d.BranchId.Value));
        }

        var rows = await query
            .OrderBy(d => d.DeviceNumber)
            .Select(d => new
            {
                Device = d,
                RegisteredBy = db.Users.Where(u => u.Id == d.RegisteredByUserId).Select(u => u.FullName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows.Select(r => ToDto(r.Device, r.RegisteredBy)).ToList();
    }

    public async Task<DeviceDto> RenameAsync(Guid id, RenameDeviceRequest request, CancellationToken ct)
    {
        await renameValidator.ValidateOrThrowAsync(request, ct);
        var device = await LoadManageableAsync(id, ct);
        device.Name = request.Name.Trim();
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(device, ct);
    }

    /// <summary>
    /// Revokes the device and every session bound to it. The device wipes its local data the next
    /// time it contacts the server (FR-OFF-31, completed in Phase 8).
    /// </summary>
    public async Task<DeviceDto> RevokeAsync(Guid id, CancellationToken ct)
    {
        var device = await LoadManageableAsync(id, ct);
        if (device.Id == user.DeviceId)
            throw new ConflictException("You cannot revoke the device you are using.");

        var now = clock.GetUtcNow().UtcDateTime;
        device.IsRevoked = true;
        device.RevokedAt = now;
        device.RevokedBy = user.UserId;

        await db.RefreshTokens
            .Where(t => t.DeviceId == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(device, ct);
    }

    private async Task<Device> LoadManageableAsync(Guid id, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Device", id);
        if (user.GetScope(Permissions.Devices.Manage) != PermissionScope.All &&
            (device.BranchId is null || !user.BranchIds.Contains(device.BranchId.Value)))
            throw new NotFoundException("Device", id);
        return device;
    }

    private async Task<DeviceDto> ToDtoAsync(Device d, CancellationToken ct)
    {
        var by = d.RegisteredByUserId is null
            ? null
            : await db.Users.Where(u => u.Id == d.RegisteredByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        return ToDto(d, by);
    }

    private static DeviceDto ToDto(Device d, string? registeredBy) => new(
        d.Id, d.DeviceCode, d.Name, d.Platform, d.Model, d.AppVersion, d.BranchId, registeredBy,
        d.RegisteredAt, d.LastSeenAt, d.IsRevoked, d.LastSyncAt, d.LastPushCount, d.LastPullCount);

    /// <summary>1 → "D01", 36 → "D10" … base-36 keeps offline numbers short.</summary>
    public static string ToDeviceCode(int number)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var s = "";
        do { s = digits[number % 36] + s; number /= 36; } while (number > 0);
        return "D" + s.PadLeft(2, '0');
    }
}

public sealed class RegisterDeviceValidator : AbstractValidator<RegisterDeviceRequest>
{
    public RegisterDeviceValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Platform).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Model).MaximumLength(100);
        RuleFor(x => x.AppVersion).NotEmpty().MaximumLength(30);
    }
}

public sealed class RenameDeviceValidator : AbstractValidator<RenameDeviceRequest>
{
    public RenameDeviceValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}
