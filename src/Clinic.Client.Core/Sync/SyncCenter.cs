using System.Text.Json.Nodes;
using Clinic.Client.Core.Local;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Sync;

/// <summary>FR-SYN-30: one field of a conflict — my value, the server's value, and whether they differ.</summary>
public sealed record ConflictField(string Field, JsonNode? Mine, JsonNode? Theirs)
{
    public bool Differs => !JsonNode.DeepEquals(Mine, Theirs);
}

/// <summary>FR-OFF-12: what the Sync Center screen can do with queued, failed and conflicted changes.</summary>
public sealed class SyncCenter(LocalDatabase db, SyncEngine engine, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<List<OutboxEntry>> ListAsync() =>
        db.Connection.Table<OutboxEntry>().OrderBy(e => e.Seq).ToListAsync();

    public Task<int> CountAsync() => db.Connection.Table<OutboxEntry>().CountAsync();

    /// <summary>Send a failed or rejected change again (e.g. after an admin granted a permission).</summary>
    public async Task RetryAsync(long seq)
    {
        var entry = await db.Connection.FindAsync<OutboxEntry>(seq);
        if (entry is null || entry.Status == OutboxStatus.Pending) return;

        entry.Status = OutboxStatus.Pending;
        entry.Attempts = 0;
        entry.NextAttemptAt = null;
        entry.LastError = null;
        await db.Connection.UpdateAsync(entry);
        await engine.RefreshStateAsync();
    }

    /// <summary>FR-SYN-30: the changed fields of a conflicted entry, side by side (Mine / Theirs).</summary>
    public async Task<IReadOnlyList<ConflictField>> ConflictFieldsAsync(long seq)
    {
        var entry = await db.Connection.FindAsync<OutboxEntry>(seq);
        if (entry is null || entry.Status != OutboxStatus.Conflict) return [];
        var mine = LocalJson.Parse(entry.Payload);
        var theirs = LocalJson.Parse(entry.ServerData);
        return LocalJson.Fields(entry.ChangedFields)
            .Select(f => new ConflictField(f, mine?[f]?.DeepClone(), theirs?[f]?.DeepClone()))
            .OrderByDescending(f => f.Differs).ToList();
    }

    /// <summary>Conflict: keep my values. Re-bases the change on the server's version so it applies.</summary>
    public async Task KeepMineAsync(long seq)
    {
        var entry = await db.Connection.FindAsync<OutboxEntry>(seq);
        if (entry is null || entry.Status != OutboxStatus.Conflict) return;

        var server = LocalJson.Parse(entry.ServerData);
        entry.BaseRowVersion = server?["rowVersion"]?.GetValue<long>() ?? entry.BaseRowVersion;
        entry.Status = OutboxStatus.Pending;
        entry.ServerData = null;
        entry.LastError = null;
        await db.Connection.RunInTransactionAsync(c =>
        {
            c.Update(entry);
            Queue(c, entry.ConflictId, ConflictResolution.Mine, LocalJson.Parse(entry.Payload));
        });
        await engine.RefreshStateAsync();
    }

    /// <summary>
    /// FR-SYN-30 "Merged": per field, the value the person accepted (mine, theirs or typed). The local row takes those
    /// values and the change is re-based on the server's version; if every field took the server's value it is the
    /// same as discarding mine.
    /// </summary>
    public async Task ResolveAsync(long seq, IReadOnlyDictionary<string, JsonNode?> accepted)
    {
        var entry = await db.Connection.FindAsync<OutboxEntry>(seq);
        if (entry is null || entry.Status != OutboxStatus.Conflict) return;
        var fields = await ConflictFieldsAsync(seq);
        var merged = new JsonObject();
        foreach (var f in fields) merged[f.Field] = (accepted.TryGetValue(f.Field, out var v) ? v : f.Mine)?.DeepClone();

        if (fields.All(f => JsonNode.DeepEquals(merged[f.Field], f.Theirs)))
        {
            await DiscardAsync(seq);
            return;
        }
        var resolution = fields.All(f => JsonNode.DeepEquals(merged[f.Field], f.Mine)) ? ConflictResolution.Mine : ConflictResolution.Merged;
        var server = LocalJson.Parse(entry.ServerData);
        var local = LocalSyncRegistry.Find(entry.EntityType);

        await db.Connection.RunInTransactionAsync(c =>
        {
            if (local is not null && c.Find(entry.RecordId, c.GetMapping(local.LocalType)) is LocalEntity row)
            {
                // Server values for the fields nobody fought over, the accepted values for the rest.
                var json = LocalJson.ToJson(row);
                foreach (var (k, value) in server ?? []) if (k is not ("rowVersion" or "id")) json[k] = value?.DeepClone();
                foreach (var (k, value) in merged) json[k] = value?.DeepClone();
                var updated = LocalJson.ToEntity(json, local.LocalType);
                updated.RowVersion = server?["rowVersion"]?.GetValue<long>() ?? row.RowVersion;
                updated.SyncStatus = SyncStatus.Pending;
                c.Update(updated, local.LocalType);
            }
            entry.Payload = merged.ToJsonString(LocalJson.Options);
            entry.ChangedFields = System.Text.Json.JsonSerializer.Serialize(merged.Select(kv => kv.Key).ToList(), LocalJson.Options);
            entry.BaseRowVersion = server?["rowVersion"]?.GetValue<long>() ?? entry.BaseRowVersion;
            entry.Status = OutboxStatus.Pending;
            entry.ServerData = null;
            entry.LastError = null;
            c.Update(entry);
            Queue(c, entry.ConflictId, resolution, merged);
        });
        await engine.RefreshStateAsync();
    }

    /// <summary>
    /// Drop my change and take the server's version (or remove a record the server never accepted).
    /// </summary>
    public async Task DiscardAsync(long seq)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await db.Connection.RunInTransactionAsync(c =>
        {
            var entry = c.Find<OutboxEntry>(seq);
            if (entry is null) return;
            if (entry.Status == OutboxStatus.Conflict) Queue(c, entry.ConflictId, ConflictResolution.Theirs, null);
            var local = LocalSyncRegistry.Find(entry.EntityType);
            c.Delete(entry);
            if (local is null) return;

            var server = LocalJson.Parse(entry.ServerData);
            var remaining = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE RecordId = ?", entry.RecordId);

            if (server is not null)
            {
                // Force the server copy in even if the local row claims a newer version.
                if (remaining == 0)
                    c.Execute($"UPDATE \"{c.GetMapping(local.LocalType).TableName}\" SET RowVersion = 0 WHERE Id = ?", entry.RecordId);
                LocalApplier.ApplyServerRow(c, local, server, now);
            }
            else if (entry.Operation == (int)SyncOperation.Insert && remaining == 0)
            {
                c.Execute($"DELETE FROM \"{c.GetMapping(local.LocalType).TableName}\" WHERE Id = ?", entry.RecordId);
            }
            else if (remaining == 0)
            {
                LocalApplier.MarkPushed(c, local, entry.RecordId, null, now);
            }
        });
        await engine.RefreshStateAsync();
    }

    /// <summary>FR-OFF-32: used only when the user holds Sync.DiscardPending and chose to sign out anyway.</summary>
    public async Task DiscardAllAsync()
    {
        foreach (var entry in (await ListAsync()).OrderByDescending(e => e.Seq))
            await DiscardAsync(entry.Seq);
    }

    private void Queue(SQLite.SQLiteConnection c, Guid? conflictId, string resolution, JsonObject? values)
    {
        if (conflictId is not { } id) return;
        c.Insert(new PendingConflictResolution
        {
            ConflictId = id, Resolution = resolution, ValuesJson = values?.ToJsonString(LocalJson.Options), ResolvedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }
}
