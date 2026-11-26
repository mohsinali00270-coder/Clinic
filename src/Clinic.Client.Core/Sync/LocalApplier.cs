using System.Text.Json.Nodes;
using Clinic.Client.Core.Local;
using Clinic.Shared.Sync;
using SQLite;

namespace Clinic.Client.Core.Sync;

/// <summary>Writes server data into local tables without losing local changes that have not been pushed yet.</summary>
internal static class LocalApplier
{
    /// <summary>
    /// Upserts a server row. Fields the device still has pending changes for keep their local value, so a
    /// pull can never undo work the user has not synced yet. Older server versions are ignored.
    /// </summary>
    public static void ApplyServerRow(SQLiteConnection c, LocalSyncEntry entry, JsonObject json, DateTime now)
    {
        var id = json["id"]!.GetValue<Guid>();
        var rowVersion = json["rowVersion"]?.GetValue<long>() ?? 0;
        var map = c.GetMapping(entry.LocalType);

        var local = c.Find(id, map) as LocalEntity;
        if (local is not null && local.RowVersion > rowVersion) return;

        var pending = c.Query<OutboxEntry>("SELECT * FROM Outbox WHERE RecordId = ?", id);
        LocalEntity incoming;

        if (pending.Count > 0 && local is not null)
        {
            var localJson = LocalJson.ToJson(local);
            var merged = (JsonObject)json.DeepClone();
            foreach (var field in pending.SelectMany(p => LocalJson.Fields(p.ChangedFields)).Distinct())
                merged[field] = localJson[field]?.DeepClone();
            if (pending.Any(p => p.Operation == (int)SyncOperation.Delete))
                merged["isDeleted"] = true;

            incoming = LocalJson.ToEntity(merged, entry.LocalType);
            incoming.SyncStatus = local.SyncStatus;
            incoming.LastSyncedAt = local.LastSyncedAt;
        }
        else
        {
            incoming = LocalJson.ToEntity(json, entry.LocalType);
            incoming.SyncStatus = SyncStatus.Synced;
            incoming.LastSyncedAt = now;
        }

        if (local is not null) CopyLocalOnly(local, incoming);
        c.InsertOrReplace(incoming, entry.LocalType);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> LocalOnlyProps = new();

    /// <summary>[LocalOnly] columns (e.g. the DynamicData snapshot) are not on the wire; keep the device copy.</summary>
    private static void CopyLocalOnly(LocalEntity from, LocalEntity to)
    {
        var props = LocalOnlyProps.GetOrAdd(from.GetType(), t => t.GetProperties()
            .Where(p => p.IsDefined(typeof(LocalOnlyAttribute), true)).ToArray());
        foreach (var p in props) p.SetValue(to, p.GetValue(from));
    }

    public static void ApplyTombstone(SQLiteConnection c, LocalSyncEntry entry, SyncTombstone tombstone, DateTime now)
    {
        if (c.Find(tombstone.RecordId, c.GetMapping(entry.LocalType)) is not LocalEntity local) return;
        if (local.RowVersion > tombstone.RowVersion) return;

        // A pending local edit wins over a remote delete (§7.4); the push will restore it on the server.
        var hasPending = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE RecordId = ?", tombstone.RecordId) > 0;
        if (hasPending) return;

        local.IsDeleted = true;
        local.RowVersion = tombstone.RowVersion;
        local.SyncStatus = SyncStatus.Synced;
        local.LastSyncedAt = now;
        c.Update(local, entry.LocalType);
    }

    /// <summary>After a change was accepted: record the new version and mark the row synced if nothing else is queued.</summary>
    public static void MarkPushed(SQLiteConnection c, LocalSyncEntry entry, Guid recordId, long? rowVersion, DateTime now)
    {
        if (c.Find(recordId, c.GetMapping(entry.LocalType)) is not LocalEntity row) return;

        var remaining = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE RecordId = ?", recordId);
        if (rowVersion is { } v && v > row.RowVersion) row.RowVersion = v;
        if (remaining == 0)
        {
            row.SyncStatus = SyncStatus.Synced;
            row.LastSyncedAt = now;
        }
        c.Update(row, entry.LocalType);
    }

    public static void SetRowStatus(SQLiteConnection c, LocalSyncEntry entry, Guid recordId, SyncStatus status)
    {
        if (c.Find(recordId, c.GetMapping(entry.LocalType)) is not LocalEntity row) return;
        row.SyncStatus = status;
        c.Update(row, entry.LocalType);
    }
}
