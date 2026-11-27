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
public sealed class SyncService(IAppDbContext db, ICurrentUser user, TimeProvider clock, IOptions<SyncOptions> options,
    IEnumerable<Clinical.IChangeNotifier> notifiers)
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

        using var activity = SyncTelemetry.Source.StartActivity("sync.push");
        activity?.SetTag("clinic.changes", request.Changes.Count);

        IReadOnlyList<SyncChangeResult> results;
        HashSet<Guid?> branches;
        try
        {
            (results, branches) = await ProcessBatchAsync(request.Changes, device.Id, ct);
        }
        catch (DbUpdateException)
        {
            // The same ChangeId arrived in two requests at once and the other one committed first: run the batch
            // again, and the ChangeIds the other request applied now come back as duplicates.
            (results, branches) = await ProcessBatchAsync(request.Changes, device.Id, ct);
        }

        // Saves inside the batch already notified, but the batch may commit after the notifier's short delay.
        if (branches.Count > 0 && db.CurrentTenantId is { } tenant)
            foreach (var n in notifiers) n.Notify(tenant, branches);

        SyncTelemetry.ChangesPushed.Add(results.Count);
        SyncTelemetry.Conflicts.Add(results.Count(r => r.Status == SyncChangeStatus.Conflict));
        SyncTelemetry.Rejections.Add(results.Count(r => r.Status == SyncChangeStatus.Rejected));

        await TouchDeviceAsync(device.Id, pushed: results.Count, pulled: null, ct);
        return new SyncPushResponse(results);
    }

    /// <summary>
    /// The batch runs in one transaction with a savepoint per change: a rejected or conflicting change is rolled
    /// back to its savepoint on its own, and the SyncLog rows of the applied changes are written once at the end.
    /// (One commit per batch instead of per change keeps a 200-change push well inside the 3 s target.)
    /// </summary>
    private async Task<(IReadOnlyList<SyncChangeResult>, HashSet<Guid?>)> ProcessBatchAsync(IReadOnlyList<SyncChange> changes, Guid deviceId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                var results = new List<SyncChangeResult>(changes.Count);
                var branches = new HashSet<Guid?>();
                var logs = new List<SyncLog>();
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var ids = changes.Select(c => c.ChangeId).Distinct().ToList();
                var known = await db.SyncLogs.AsNoTracking().Where(l => ids.Contains(l.ChangeId))
                    .Select(l => new { l.ChangeId, l.RowVersion }).ToDictionaryAsync(l => l.ChangeId, l => l.RowVersion, ct);

                for (var i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    if (known.TryGetValue(change.ChangeId, out var previous))
                    {
                        results.Add(new SyncChangeResult(change.ChangeId, SyncChangeStatus.Duplicate, previous));
                        continue;
                    }

                    var savepoint = $"c{i}";
                    await tx.CreateSavepointAsync(savepoint, ct);
                    db.ChangeTracker.Clear();
                    var outcome = await ApplyAsync(change, deviceId, ct);
                    if (outcome.Status != SyncChangeStatus.Applied)
                    {
                        // Rejections and conflicts are not logged: the device may fix the cause and resend the same ChangeId.
                        db.ChangeTracker.Clear();
                        await tx.RollbackToSavepointAsync(savepoint, ct);
                        if (outcome.Status == SyncChangeStatus.Conflict)
                            outcome = outcome with { ConflictId = await RecordConflictAsync(change, deviceId, outcome, ct) };
                        results.Add(outcome);
                        continue;
                    }

                    foreach (var e in db.ChangeTracker.Entries<ISyncEntity>()) branches.Add(e.Entity.GetSyncBranchId());
                    known[change.ChangeId] = outcome.RowVersion ?? 0;
                    logs.Add(new SyncLog
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
                    results.Add(outcome);
                }

                db.ChangeTracker.Clear();
                db.SyncLogs.AddRange(logs);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ((IReadOnlyList<SyncChangeResult>)results, branches);
            });
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
                await RedirectMergedPatientAsync(entity);

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
                                $"Changed by someone else: {string.Join(", ", overlap)}", Note: string.Join(",", overlap), ServerData: Serialize(existing));
                        case ConflictPolicy.LastWriterWins:
                            notes.Add($"Overwrote newer values from another device: {string.Join(", ", overlap)}.");
                            break;
                    }
                }

                error = SetFields(existing, entityType, descriptor, change.ChangedFields, change.Data);
                if (error is not null) return Reject(error, existing);
                await RedirectMergedPatientAsync(existing);

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

    /// <summary>FR-SYN-32: a device that has not pulled a merge yet still sends the duplicate's id; point it at the survivor.</summary>
    private async Task RedirectMergedPatientAsync(object entity)
    {
        if (entity is Domain.Clinical.Patient) return;
        var prop = entity.GetType().GetProperty("PatientId");
        if (prop?.GetValue(entity) is not Guid patientId) return;
        if (await Clinical.PatientMergeService.SurvivorOfAsync(db, patientId) is { } survivor) prop.SetValue(entity, survivor);
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
        SyncTelemetry.RowsPulled.Add(entities.Sum(e => e.Value.Count) + tombstones.Count);
        return new SyncPullResponse(checkpoint, hasMore, entities, tombstones);
    }

    /// <summary>
    /// FR-SYN-23: everything the device may see, as gzip-compressed NDJSON — one pull page per line, the last one
    /// carrying the checkpoint to continue from. Built from the same pages as pull, so the rules are identical.
    /// </summary>
    public async Task WriteSnapshotAsync(Guid deviceId, string appVersion, Stream output, CancellationToken ct)
    {
        // Compressed into a temporary file first: GZipStream finishes with a synchronous write, which the response
        // stream does not allow, and a file keeps a large clinic's snapshot out of memory.
        var path = Path.Combine(Path.GetTempPath(), $"clinic-snapshot-{Guid.NewGuid():N}.ndjson.gz");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                await using (var writer = new StreamWriter(gzip, new System.Text.UTF8Encoding(false)))
                {
                    long since = 0;
                    while (true)
                    {
                        var page = await PullAsync(deviceId, appVersion, since, _options.MaxPullPage, ct);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(page, Json));
                        db.ChangeTracker.Clear();
                        if (!page.HasMore || page.ResetRequired) break;
                        since = page.Checkpoint;
                    }
                }
                file.Position = 0;
                await file.CopyToAsync(output, ct);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // =====================================================================
    // Conflicts (FR-SYN-30/31)
    // =====================================================================

    /// <summary>Stores (or reuses, for a re-sent ChangeId) the conflict so it can be listed and audited when resolved.</summary>
    private async Task<Guid> RecordConflictAsync(SyncChange change, Guid deviceId, SyncChangeResult outcome, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var existing = await db.SyncConflicts.FirstOrDefaultAsync(c => c.ChangeId == change.ChangeId && c.Status == "open", ct);
        var fields = (outcome.Note ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        JsonObject Subset(JsonObject? source)
        {
            var o = new JsonObject();
            foreach (var f in fields) o[f] = source?[f]?.DeepClone();
            return o;
        }
        var conflict = existing ?? new SyncConflict { Id = Guid.CreateVersion7(), ChangeId = change.ChangeId, CreatedAt = clock.GetUtcNow().UtcDateTime };
        conflict.TenantId = db.CurrentTenantId!.Value;
        conflict.EntityType = change.EntityType;
        conflict.RecordId = change.RecordId;
        conflict.Fields = string.Join(",", fields);
        conflict.MineJson = Subset(change.Data).ToJsonString();
        conflict.TheirsJson = Subset(outcome.ServerData).ToJsonString();
        conflict.BaseRowVersion = change.BaseRowVersion;
        conflict.ServerRowVersion = outcome.RowVersion ?? 0;
        conflict.DeviceId = deviceId;
        conflict.UserId = user.UserId;
        if (existing is null) db.SyncConflicts.Add(conflict);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return conflict.Id;
    }

    /// <summary>Closes conflicts settled on the device and writes the audit row with mine, theirs and the outcome.</summary>
    public async Task ResolveConflictsAsync(IReadOnlyList<ConflictResolutionReport> reports, CancellationToken ct)
    {
        foreach (var r in reports)
        {
            if (!ConflictResolution.All.Contains(r.Resolution)) throw new RequestValidationException("resolution", $"Unknown resolution '{r.Resolution}'.");
            var c = await db.SyncConflicts.FirstOrDefaultAsync(x => x.Id == r.ConflictId, ct);
            if (c is null || c.Status != "open") continue; // already closed (a resend) or not this tenant's
            if (c.UserId != user.UserId && user.GetScope(Clinic.Shared.Security.Permissions.Audit.View) is null)
                throw new ForbiddenException("Only the user who made the change, or an auditor, can resolve this conflict.");
            c.Status = "resolved";
            c.Resolution = r.Resolution;
            c.ResolvedJson = r.Values?.ToJsonString() ?? (r.Resolution == ConflictResolution.Theirs ? c.TheirsJson : c.MineJson);
            c.ResolvedAt = r.ResolvedAt.UtcDateTime;
            c.ResolvedBy = user.UserId;
            db.AuditLogs.Add(new Domain.Auditing.AuditLog
            {
                TenantId = c.TenantId, UserId = user.UserId, DeviceId = user.DeviceId, Action = $"ConflictResolved:{r.Resolution}",
                EntityType = c.EntityType, EntityId = c.RecordId.ToString(),
                OldValues = new JsonObject { ["mine"] = JsonNode.Parse(c.MineJson ?? "{}"), ["theirs"] = JsonNode.Parse(c.TheirsJson ?? "{}") }.ToJsonString(),
                NewValues = c.ResolvedJson, Timestamp = clock.GetUtcNow().UtcDateTime,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConflictDto>> ConflictsAsync(string? status, CancellationToken ct)
    {
        var q = db.SyncConflicts.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Take(500).ToListAsync(ct);
        static JsonObject? Obj(string? s) => s is null ? null : JsonNode.Parse(s) as JsonObject;
        return rows.Select(c => new ConflictDto(c.Id, c.EntityType, c.RecordId, c.Fields.Split(',', StringSplitOptions.RemoveEmptyEntries), Obj(c.MineJson),
            Obj(c.TheirsJson), c.DeviceId, c.UserId, c.CreatedAt, c.Status, c.Resolution, Obj(c.ResolvedJson), c.ResolvedAt)).ToList();
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
