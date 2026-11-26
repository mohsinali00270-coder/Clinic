using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Sync;
using Clinic.Shared.Sync;
using SQLite;

namespace Clinic.Client.Core.Local;

/// <summary>
/// The only way screens write data. FR-OFF-10: every write updates the row and appends an outbox entry
/// in one SQLite transaction, so a crash can never leave a change that will not sync (or vice versa).
/// <see cref="WriteAsync"/> groups several writes (a record and its dynamic values) into one transaction.
/// </summary>
public sealed class LocalStore(LocalDatabase db, IClientContext context, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Raised after any local write with the entity type name. The app debounces a sync on it.</summary>
    public event EventHandler<string>? Changed;

    public LocalDatabase Database => db;
    public IClientContext Context => context;

    public Task<List<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : LocalEntity, new()
    {
        var query = db.Connection.Table<T>().Where(e => !e.IsDeleted);
        if (where is not null) query = query.Where(where);
        return query.ToListAsync();
    }

    public async Task<T?> GetAsync<T>(Guid id) where T : LocalEntity, new()
    {
        var row = await db.Connection.FindAsync<T>(id);
        return row is { IsDeleted: false } ? row : null;
    }

    /// <summary>Runs several writes in one transaction, then raises <see cref="Changed"/> once per entity type.</summary>
    public async Task WriteAsync(Action<LocalWriteBatch> work)
    {
        LocalWriteBatch? batch = null;
        await db.Connection.RunInTransactionAsync(c =>
        {
            batch = new LocalWriteBatch(c, context, _clock.GetUtcNow().UtcDateTime);
            work(batch);
        });
        foreach (var type in batch!.ChangedTypes) Changed?.Invoke(this, type);
    }

    public async Task<T> InsertAsync<T>(T entity) where T : LocalEntity, new()
    {
        await WriteAsync(b => b.Insert(entity));
        return entity;
    }

    /// <summary>Saves an edited row. Only fields that really changed go into the outbox (field-level merge).</summary>
    public async Task<bool> UpdateAsync<T>(T entity) where T : LocalEntity, new()
    {
        var changed = false;
        await WriteAsync(b => changed = b.Update(entity));
        return changed;
    }

    public Task DeleteAsync<T>(Guid id) where T : LocalEntity, new() => WriteAsync(b => b.Delete<T>(id));
}

/// <summary>Writes inside one SQLite transaction; each change also appends its outbox entry.</summary>
public sealed class LocalWriteBatch
{
    private readonly SQLiteConnection _c;
    private readonly IClientContext _context;
    private readonly DateTime _now;

    internal LocalWriteBatch(SQLiteConnection c, IClientContext context, DateTime now)
    {
        _c = c;
        _context = context;
        _now = now;
    }

    public HashSet<string> ChangedTypes { get; } = [];
    public SQLiteConnection Connection => _c;

    public T? Find<T>(Guid id) where T : LocalEntity, new() => _c.Find<T>(id);

    public T Insert<T>(T entity) where T : LocalEntity, new()
    {
        var entry = Writable<T>();
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        entity.TenantId = _context.TenantId;
        entity.CreatedAt = _now;
        entity.CreatedBy = _context.UserId;
        entity.RowVersion = 0;
        entity.IsDeleted = false;
        entity.SyncStatus = SyncStatus.Pending;

        var json = LocalJson.ToJson(entity);
        var data = Subset(json, entry.WritableFields);
        _c.Insert(entity);
        _c.Insert(NewEntry(entry, entity, SyncOperation.Insert, null, data.Select(kv => kv.Key), data));
        ChangedTypes.Add(entry.Name);
        return entity;
    }

    /// <summary>Returns false when nothing that syncs changed.</summary>
    public bool Update<T>(T entity) where T : LocalEntity, new()
    {
        var entry = Writable<T>();
        var existing = _c.Find<T>(entity.Id)
                       ?? throw new InvalidOperationException($"{entry.Name} {entity.Id} does not exist locally.");

        var before = LocalJson.ToJson(existing);
        var after = LocalJson.ToJson(entity);
        var changed = entry.WritableFields.Where(f => !JsonNode.DeepEquals(before[f], after[f])).ToList();

        entity.TenantId = existing.TenantId;
        entity.CreatedAt = existing.CreatedAt;
        entity.CreatedBy = existing.CreatedBy;
        entity.RowVersion = existing.RowVersion;
        entity.IsDeleted = existing.IsDeleted;
        entity.LastSyncedAt = existing.LastSyncedAt;

        if (changed.Count == 0)
        {
            // Local-only columns (e.g. a snapshot) may still have changed.
            entity.UpdatedAt = existing.UpdatedAt;
            entity.UpdatedBy = existing.UpdatedBy;
            entity.SyncStatus = existing.SyncStatus;
            _c.Update(entity);
            return false;
        }

        entity.UpdatedAt = _now;
        entity.UpdatedBy = _context.UserId;
        entity.SyncStatus = existing.SyncStatus == SyncStatus.Conflict ? SyncStatus.Conflict : SyncStatus.Pending;

        _c.Update(entity);
        _c.Insert(NewEntry(entry, entity, SyncOperation.Update, existing.RowVersion, changed, Subset(after, changed)));
        ChangedTypes.Add(entry.Name);
        return true;
    }

    public void Delete<T>(Guid id) where T : LocalEntity, new()
    {
        var entry = Writable<T>();
        var existing = _c.Find<T>(id);
        if (existing is null || existing.IsDeleted) return;

        var pending = _c.Table<OutboxEntry>().Where(o => o.RecordId == id).ToList();
        var neverSent = existing.RowVersion == 0 &&
                        pending.Any(p => p.Operation == (int)SyncOperation.Insert) &&
                        pending.All(p => p.Status == OutboxStatus.Pending && p.Attempts == 0);

        ChangedTypes.Add(entry.Name);
        if (neverSent)
        {
            // The server never saw this record: just drop it and its queued changes.
            foreach (var p in pending) _c.Delete(p);
            _c.Delete(existing);
            return;
        }

        existing.IsDeleted = true;
        existing.UpdatedAt = _now;
        existing.UpdatedBy = _context.UserId;
        existing.SyncStatus = SyncStatus.Pending;
        _c.Update(existing);
        _c.Insert(NewEntry(entry, existing, SyncOperation.Delete, existing.RowVersion, [], null));
    }

    private static LocalSyncEntry Writable<T>()
    {
        var entry = LocalSyncRegistry.For(typeof(T));
        if (!entry.ClientWritable)
            throw new InvalidOperationException($"{entry.Name} records are managed on the server and cannot be edited on the device.");
        return entry;
    }

    private OutboxEntry NewEntry(LocalSyncEntry entry, LocalEntity entity, SyncOperation op, long? baseVersion,
        IEnumerable<string> fields, JsonObject? data) => new()
    {
        ChangeId = Guid.NewGuid(),
        EntityType = entry.Name,
        RecordId = entity.Id,
        Operation = (int)op,
        ChangedFields = JsonSerializer.Serialize(fields.ToList(), LocalJson.Options),
        Payload = data?.ToJsonString(LocalJson.Options),
        BaseRowVersion = baseVersion,
        DeviceId = _context.DeviceId,
        UserId = _context.UserId,
        ClientTimestamp = _now,
        Status = OutboxStatus.Pending,
        Summary = $"{op} {LocalJson.Describe(entity, entry.Name)}",
    };

    private static JsonObject Subset(JsonObject source, IEnumerable<string> fields)
    {
        var result = new JsonObject();
        foreach (var f in fields)
            result[f] = source[f]?.DeepClone();
        return result;
    }
}
