using Clinic.Client.Core.Local;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Sync;

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
        await db.Connection.UpdateAsync(entry);
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
}
