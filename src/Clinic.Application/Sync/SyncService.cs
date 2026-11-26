using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Application.Forms;
using Clinic.Domain.Devices;
using Clinic.Domain.Sync;
using Clinic.Shared.Contracts;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;

namespace Clinic.Application.Sync;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>FR-SYN-44: older apps must update before they may sync.</summary>
    public string MinAppVersion { get; set; } = "1.0";
    public int MaxPushBatch { get; set; } = 200;
    public int MaxPullPage { get; set; } = 500;
}

/// <summary>Thrown when an app below the minimum version tries to sync (mapped to 426).</summary>
public sealed class UpdateRequiredException(string minVersion)
    : Exception($"This app version is no longer supported. Update to {minVersion} or later to sync.")
{
    public string MinVersion { get; } = minVersion;
}

/// <summary>
/// §7: push-then-pull, incremental, idempotent. The DbContext assigns RowVersion and writes ChangeLog rows
/// on every save of a sync entity, so this service only decides what to apply.
/// </summary>
public sealed class SyncService(IAppDbContext db, ICurrentUser user, TimeProvider clock, IOptions<SyncOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly MethodInfo FindMethod = typeof(SyncService).GetMethod(nameof(FindGenericAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo LoadMethod = typeof(SyncService).GetMethod(nameof(LoadGenericAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SyncOptions _options = options.Value;

    // =====================================================================
    // Push (FR-SYN-10 … 14)
    // =====================================================================

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct)
    {
        var device = await RequireDeviceAsync(request.DeviceId, request.AppVersion, ct);

        if (request.Changes.Count > _options.MaxPushBatch)
            throw new RequestValidationException("changes", $"Send at most {_options.MaxPushBatch} changes per batch.");

        var results = new List<SyncChangeResult>(request.Changes.Count);
        foreach (var change in request.Changes)
            results.Add(await ProcessAsync(change, device.Id, ct));

        db.ChangeTracker.Clear();
        await TouchDeviceAsync(device.Id, pushed: results.Count, pulled: null, ct);
        return new SyncPushResponse(results);
    }

    private async Task<SyncChangeResult> ProcessAsync(SyncChange change, Guid deviceId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var previous = await db.SyncLogs.AsNoTracking().FirstOrDefaultAsync(l => l.ChangeId == change.ChangeId, ct);
                if (previous is not null)
                    return new SyncChangeResult(change.ChangeId, SyncChangeStatus.Duplicate, previous.RowVersion);

                var outcome = await ApplyAsync(change, deviceId, ct);
                if (outcome.Status != SyncChangeStatus.Applied)
                {
                    // Rejections and conflicts are not logged: the device may fix the cause and resend the same ChangeId.
                    await tx.RollbackAsync(ct);
                    return outcome;
                }

                db.SyncLogs.Add(new SyncLog
                {
                    ChangeId = change.ChangeId,
                    TenantId = db.CurrentTenantId!.Value,
                    DeviceId = deviceId,
                    UserId = user.UserId,
                    EntityType = change.EntityType,
                    RecordId = change.RecordId,
                    Status = outcome.Status.ToString(),
                    RowVersion = outcome.RowVersion,
                    ClientTimestamp = change.ClientTimestamp.UtcDateTime,
                    ProcessedAt = clock.GetUtcNow().UtcDateTime,
                });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return outcome;
            });
        }
        catch (DbUpdateException)
        {
            // The same ChangeId arrived twice at once and the other request committed first.
            db.ChangeTracker.Clear();
            var applied = await db.SyncLogs.AsNoTracking().FirstOrDefaultAsync(l => l.ChangeId == change.ChangeId, ct);
            if (applied is not null)
                return new SyncChangeResult(change.ChangeId, SyncChangeStatus.Duplicate, applied.RowVersion);
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<SyncChangeResult> ApplyAsync(SyncChange change, Guid deviceId, CancellationToken ct)
    {
        SyncChangeResult Reject(string error, ISyncEntity? current = null) =>
            new(change.ChangeId, SyncChangeStatus.Rejected, current?.RowVersion, error, ServerData: current is null ? null : Serialize(current));

        var descriptor = SyncRegistry.Find(change.EntityType);
        if (descriptor is null) return Reject($"Unknown entity type '{change.EntityType}'.");
        if (!descriptor.ClientWritable) return Reject($"{descriptor.Name} records can only be changed on the server.");
        if (descriptor.WritePermission != FieldValueRules.WritePermission && user.GetScope(descriptor.WritePermission!) is null)
            return Reject($"You do not have the {descriptor.WritePermission} permission.");

        var entityType = db.Model.FindEntityType(descriptor.ClrType)!;
        var existing = await FindAsync(descriptor, change.RecordId, ct);

        switch (change.Operation)
        {
            case SyncOperation.Insert when existing is not null && descriptor.UpsertOnInsert:
            {
                // Deterministic ids: another device created the same row offline. Apply this insert as an edit.
                var upsertFields = change.Data?.Select(kv => kv.Key).ToList() ?? [];
                existing.IsDeleted = false;
                var overlap = await OverlappingFieldsAsync(change with { ChangedFields = upsertFields }, existing, deviceId, ct);
                var error = SetFields(existing, entityType, descriptor, upsertFields, change.Data)
                            ?? descriptor.Validate(existing)
                            ?? await descriptor.Authorize(new(existing, false, user, db));
                if (error is not null) return Reject(error, existing);

                await db.SaveChangesAsync(ct);
                if (descriptor.AfterApplyAsync is { } afterUpsert) await afterUpsert(new(existing, false, user, db));
                return new(change.ChangeId, SyncChangeStatus.Applied, existing.RowVersion,
                    Note: overlap.Count > 0 ? $"Merged with a value set on another device: {string.Join(", ", overlap)}." : null);
            }

            case SyncOperation.Insert:
            {
                if (existing is not null)
                    return Reject("A record with this id already exists.", existing);

                var entity = (ISyncEntity)Activator.CreateInstance(descriptor.ClrType)!;
                ((Domain.Common.Entity)entity).Id = change.RecordId;
                var fields = change.Data?.Select(kv => kv.Key) ?? [];
                var error = SetFields(entity, entityType, descriptor, fields, change.Data);
                if (error is not null) return Reject(error);

                error = descriptor.Validate(entity) ?? await descriptor.Authorize(new(entity, true, user, db));
                if (error is not null) return Reject(error);

                db.Entry(entity).State = EntityState.Added;
                await db.SaveChangesAsync(ct);
                if (descriptor.AfterApplyAsync is { } afterInsert) await afterInsert(new(entity, true, user, db));
                return new(change.ChangeId, SyncChangeStatus.Applied, entity.RowVersion);
            }

            case SyncOperation.Update:
            {
                if (existing is null) return Reject("The record does not exist on the server.");

                var error = await descriptor.Authorize(new(existing, false, user, db, SyncStage.BeforeChange));
                if (error is not null) return Reject(error, existing);

                var notes = new List<string>();
                if (existing.IsDeleted)
                {
                    // §7.4 delete vs edit: the edit wins and the record comes back.
                    existing.IsDeleted = false;
                    notes.Add("Record was deleted elsewhere and has been restored by this edit.");
                }

                var overlap = await OverlappingFieldsAsync(change, existing, deviceId, ct);
                if (overlap.Count > 0)
                {
                    switch (descriptor.PolicyOf(existing))
                    {
                        case ConflictPolicy.ServerWins:
                            return Reject("This record was changed on the server; your change was not applied.", existing);
                        case ConflictPolicy.Manual:
                            return new(change.ChangeId, SyncChangeStatus.Conflict, existing.RowVersion,
                                $"Changed by someone else: {string.Join(", ", overlap)}", ServerData: Serialize(existing));
                        case ConflictPolicy.LastWriterWins:
                            notes.Add($"Overwrote newer values from another device: {string.Join(", ", overlap)}.");
                            break;
                    }
                }

                error = SetFields(existing, entityType, descriptor, change.ChangedFields, change.Data);
                if (error is not null) return Reject(error, existing);

                error = descriptor.Validate(existing) ?? await descriptor.Authorize(new(existing, false, user, db));
                if (error is not null) return Reject(error, existing);

                await db.SaveChangesAsync(ct);
                if (descriptor.AfterApplyAsync is { } afterUpdate) await afterUpdate(new(existing, false, user, db));
                return new(change.ChangeId, SyncChangeStatus.Applied, existing.RowVersion,
                    Note: notes.Count == 0 ? null : string.Join(" ", notes));
            }

            case SyncOperation.Delete:
            {
                if (existing is null || existing.IsDeleted)
                    return new(change.ChangeId, SyncChangeStatus.Applied, existing?.RowVersion, Note: "Already deleted.");

                var error = await descriptor.Authorize(new(existing, false, user, db, SyncStage.Delete));
                if (error is not null) return Reject(error, existing);

                var editedElsewhere = await db.ChangeLogs.AnyAsync(c =>
                    c.RecordId == change.RecordId && c.Sequence > (change.BaseRowVersion ?? 0) && c.DeviceId != deviceId, ct);
                if (editedElsewhere)
                {
                    // §7.4: edit wins over delete; the device gets the record back on its next pull.
                    // §7.4: edit wins over delete. Nothing changed on the server, so a pull would not resend the row:
                    // return it so the device can restore its local copy.
                    return new(change.ChangeId, SyncChangeStatus.Applied, existing.RowVersion,
                        Note: "Not deleted: the record was edited on another device after you opened it.",
                        ServerData: Serialize(existing));
                }

                existing.IsDeleted = true;
                await db.SaveChangesAsync(ct);
                if (descriptor.AfterApplyAsync is { } afterDelete) await afterDelete(new(existing, false, user, db));
                return new(change.ChangeId, SyncChangeStatus.Applied, existing.RowVersion);
            }

            default:
                return Reject($"Unknown operation '{change.Operation}'.");
        }
    }

    /// <summary>
    /// FR-SYN-30: fields this change touches that someone else (another device or the admin API) changed
    /// after the device's base version. Changes from the same device never conflict with each other.
    /// </summary>
    private async Task<List<string>> OverlappingFieldsAsync(SyncChange change, ISyncEntity existing, Guid deviceId, CancellationToken ct)
    {
        var baseVersion = change.BaseRowVersion ?? 0;
        if (existing.RowVersion <= baseVersion) return [];

        var others = await db.ChangeLogs
            .Where(c => c.RecordId == change.RecordId && c.Sequence > baseVersion && c.DeviceId != deviceId)
            .Select(c => c.ChangedFields)
            .ToListAsync(ct);

        var serverFields = others.SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return change.ChangedFields.Where(serverFields.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? SetFields(object entity, IEntityType entityType, SyncEntityDescriptor descriptor,
        IEnumerable<string> fields, JsonObject? data)
    {
        foreach (var field in fields.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!descriptor.WritableFields.Contains(field))
                continue; // bookkeeping and read-only fields from the device are ignored, never trusted

            var property = entityType.GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
            if (property?.PropertyInfo is null) return $"Unknown field '{field}'.";

            JsonNode? node = null;
            data?.TryGetPropertyValue(field, out node);
            try
            {
                var value = node is null ? null : node.Deserialize(property.ClrType, Json);
                if (value is null && !property.IsNullable) return $"Field '{field}' is required.";
                property.PropertyInfo.SetValue(entity, value);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return $"Field '{field}' has an invalid value.";
            }
        }
        return null;
    }

    // =====================================================================
    // Pull (FR-SYN-20 … 24)
    // =====================================================================

    public async Task<SyncPullResponse> PullAsync(Guid deviceId, string appVersion, long since, int pageSize, CancellationToken ct)
    {
        await RequireDeviceAsync(deviceId, appVersion, ct);
        pageSize = Math.Clamp(pageSize, 1, _options.MaxPullPage);

        // Read the counter first: every sequence at or below it has committed and is visible below.
        var counter = await db.SyncCounters.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == db.CurrentTenantId, ct);
        var current = counter?.Value ?? 0;
        if (since < 0 || since > current || (since > 0 && since < (counter?.PurgedThrough ?? 0)))
            return new SyncPullResponse(0, false, new Dictionary<string, List<JsonObject>>(), [], ResetRequired: true);

        var branches = user.BranchIds;
        var rows = await db.ChangeLogs
            .Where(c => c.Sequence > since && (c.BranchId == null || branches.Contains(c.BranchId.Value)))
            .OrderBy(c => c.Sequence)
            .Take(pageSize + 1)
            .Select(c => new { c.Sequence, c.EntityType, c.RecordId })
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        var page = rows.Take(pageSize).ToList();
        var checkpoint = page.Count > 0 ? page[^1].Sequence : since;
        if (!hasMore) checkpoint = Math.Max(checkpoint, current);

        var entities = new Dictionary<string, List<JsonObject>>();
        var tombstones = new List<SyncTombstone>();

        foreach (var group in page.GroupBy(r => r.EntityType)
                     .Select(g => (Descriptor: SyncRegistry.Find(g.Key), Ids: g.Select(r => r.RecordId).Distinct().ToList()))
                     .Where(g => g.Descriptor is not null)
                     .OrderBy(g => g.Descriptor!.Order))
        {
            var d = group.Descriptor!;
            if (d.ViewPermission is not null && user.GetScope(d.ViewPermission) is null) continue;

            IReadOnlyList<ISyncEntity> loaded = await LoadAsync(d, group.Ids, ct);
            if (d.FilterPullAsync is { } filter) loaded = await filter(loaded, new SyncReadContext(user, db));
            foreach (var entity in loaded)
            {
                if (entity.IsDeleted)
                    tombstones.Add(new SyncTombstone(d.Name, entity.Id, entity.RowVersion));
                else
                    (entities.TryGetValue(d.Name, out var list) ? list : entities[d.Name] = []).Add(Serialize(entity));
            }
        }

        await TouchDeviceAsync(deviceId, pushed: null, pulled: entities.Sum(e => e.Value.Count) + tombstones.Count, ct);
        return new SyncPullResponse(checkpoint, hasMore, entities, tombstones);
    }

    public async Task<SyncStatusResponse> StatusAsync(CancellationToken ct)
    {
        var current = await db.SyncCounters.Where(c => c.TenantId == db.CurrentTenantId).Select(c => (long?)c.Value).FirstOrDefaultAsync(ct) ?? 0;
        return new SyncStatusResponse(current, _options.MinAppVersion, clock.GetUtcNow().UtcDateTime, _options.MaxPushBatch, _options.MaxPullPage);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>The device in the request must be the caller's own registered, non-revoked device.</summary>
    private async Task<Device> RequireDeviceAsync(Guid deviceId, string appVersion, CancellationToken ct)
    {
        if (!IsSupported(appVersion)) throw new UpdateRequiredException(_options.MinAppVersion);

        if (user.DeviceId != deviceId)
            throw new ForbiddenException("Sign in on this device before syncing it.");

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                     ?? throw new ForbiddenException("This device is not registered. Sign in again to register it.");
        if (device.IsRevoked)
            throw new AuthFailedException("This device has been revoked by an administrator.", ApiRoutes.Problems.DeviceRevoked);
        return device;
    }

    private bool IsSupported(string appVersion) =>
        Version.TryParse(appVersion, out var v) && Version.TryParse(_options.MinAppVersion, out var min) && v >= min;

    private async Task TouchDeviceAsync(Guid deviceId, int? pushed, int? pulled, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return;
        var now = clock.GetUtcNow().UtcDateTime;
        device.LastSeenAt = now;
        device.LastSyncAt = now;
        if (pushed is { } p) device.LastPushCount = p;
        if (pulled is { } q) device.LastPullCount = q;
        await db.SaveChangesAsync(ct);
    }

    private JsonObject Serialize(object entity)
    {
        var descriptor = SyncRegistry.ForType(entity.GetType());
        var obj = new JsonObject();
        foreach (var p in db.Model.FindEntityType(entity.GetType())!.GetProperties())
        {
            if (p.PropertyInfo is null) continue;
            var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            if (descriptor.HiddenFields.Contains(name)) continue;
            obj[name] = JsonSerializer.SerializeToNode(p.PropertyInfo.GetValue(entity), p.ClrType, Json);
        }
        return obj;
    }

    private Task<ISyncEntity?> FindAsync(SyncEntityDescriptor d, Guid id, CancellationToken ct) =>
        (Task<ISyncEntity?>)FindMethod.MakeGenericMethod(d.ClrType).Invoke(this, [id, ct])!;

    private async Task<ISyncEntity?> FindGenericAsync<T>(Guid id, CancellationToken ct) where T : class, ISyncEntity
    {
        var tenant = db.CurrentTenantId;
        // Include soft-deleted rows (needed for delete-vs-edit), but never another tenant's.
        return await db.Set<T>().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenant, ct);
    }

    private Task<List<ISyncEntity>> LoadAsync(SyncEntityDescriptor d, List<Guid> ids, CancellationToken ct) =>
        (Task<List<ISyncEntity>>)LoadMethod.MakeGenericMethod(d.ClrType).Invoke(this, [ids, ct])!;

    private async Task<List<ISyncEntity>> LoadGenericAsync<T>(List<Guid> ids, CancellationToken ct) where T : class, ISyncEntity
    {
        var tenant = db.CurrentTenantId;
        var rows = await db.Set<T>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.TenantId == tenant)
            .ToListAsync(ct);
        return rows.Cast<ISyncEntity>().ToList();
    }
}
