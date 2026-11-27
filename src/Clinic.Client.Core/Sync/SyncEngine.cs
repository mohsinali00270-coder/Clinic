using System.Text.Json;
using Clinic.Client.Core.Local;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Sync;

public enum SyncPhase
{
    Idle,
    Syncing,

    /// <summary>Server unreachable; everything keeps working locally.</summary>
    Offline,
    Error,
    SignInRequired,
    UpdateRequired,
}

/// <summary>What the status bar and Sync Center show (FR-OFF-12, FR-OFF-20).</summary>
public sealed record SyncState(
    SyncPhase Phase,
    int Pending,
    int Failed,
    int Conflicts,
    int Rejected,
    DateTime? LastSuccessAt,
    string? Message)
{
    public int Outstanding => Pending + Failed + Conflicts + Rejected;
    public int NeedsAttention => Conflicts + Rejected;
}

public sealed class SyncEngineOptions
{
    public int PushBatchSize { get; set; } = 200;
    public int PullPageSize { get; set; } = 500;
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>FR-OFF-22: number kinds kept leased, how many per lease, and when to top up.</summary>
    public IReadOnlyList<string> LeaseKinds { get; set; } = NumberRangeKinds.All.ToList();
    public int LeaseSize { get; set; } = 500;
    public double TopUpThreshold { get; set; } = 0.2;

    /// <summary>FR-SYN-23: start an empty device from the server snapshot instead of paging from zero.</summary>
    public bool UseSnapshot { get; set; } = true;
}

/// <summary>
/// §7 device side: push the outbox, then pull changes since the checkpoint, then top up number ranges.
/// One run at a time; never blocks the UI (FR-SYN-42); safe to kill at any point (FR-SYN-21, FR-SYN-40).
/// </summary>
public sealed class SyncEngine(
    LocalDatabase db,
    ISyncTransport transport,
    IClientContext context,
    SyncEngineOptions? options = null,
    TimeProvider? clock = null)
{
    public const string CheckpointScope = "main";
    private const string LastSuccessKey = "last_sync_success";

    private readonly SyncEngineOptions _options = options ?? new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncState State { get; private set; } = new(SyncPhase.Idle, 0, 0, 0, 0, null, null);

    public event EventHandler<SyncState>? StateChanged;

    /// <summary>Raised with the entity types whose local rows changed during a run, so screens can reload.</summary>
    public event EventHandler<IReadOnlyCollection<string>>? DataChanged;

    /// <summary>FR-OFF-31: the server revoked this device. The app must wipe the local database.</summary>
    public event EventHandler? DeviceRevoked;

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Runs one sync. A manual run ("Sync now") skips the retry back-off.</summary>
    public async Task<SyncState> SyncAsync(bool manual = false, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return State;

        var changedTypes = new HashSet<string>();
        try
        {
            await PublishAsync(SyncPhase.Syncing, null);

            if (manual)
                await db.Connection.ExecuteAsync("UPDATE Outbox SET NextAttemptAt = NULL WHERE Status = ?", (int)OutboxStatus.Failed);

            await PushAsync(changedTypes, ct);
            await ReportResolutionsAsync(ct);
            await PullAsync(changedTypes, ct);
            await TopUpNumbersAsync(ct);

            await db.SetMetaAsync(LastSuccessKey, Now.ToString("O"));
            await PublishAsync(SyncPhase.Idle, null);
        }
        catch (SyncTransportException ex)
        {
            switch (ex.Kind)
            {
                case SyncFailureKind.DeviceRevoked:
                    await PublishAsync(SyncPhase.Error, ex.Message);
                    DeviceRevoked?.Invoke(this, EventArgs.Empty);
                    break;
                case SyncFailureKind.Unauthorized:
                    await PublishAsync(SyncPhase.SignInRequired, "Sign in again to continue syncing.");
                    break;
                case SyncFailureKind.UpdateRequired:
                    await PublishAsync(SyncPhase.UpdateRequired, ex.Message);
                    break;
                case SyncFailureKind.Transient:
                    await PublishAsync(SyncPhase.Offline, ex.Message);
                    break;
                default:
                    await PublishAsync(SyncPhase.Error, ex.Message);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PublishAsync(SyncPhase.Error, $"Sync failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            if (changedTypes.Count > 0) DataChanged?.Invoke(this, changedTypes);
        }

        return State;
    }

    /// <summary>Recomputes the outbox counts without syncing (after local saves or Sync Center actions).</summary>
    public Task RefreshStateAsync() => PublishAsync(State.Phase, State.Message);

    // =====================================================================
    // Push
    // =====================================================================

    private async Task PushAsync(HashSet<string> changedTypes, CancellationToken ct)
    {
        while (true)
        {
            var now = Now;

            // A batch is backing off after a transient failure: wait for it, to keep per-record order.
            var backingOff = await db.Connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Outbox WHERE Status = ? AND NextAttemptAt > ?", (int)OutboxStatus.Failed, now.Ticks);
            if (backingOff > 0) return;

            // Entries behind a conflict/rejection of the same record wait until a person resolves it.
            var batch = await db.Connection.QueryAsync<OutboxEntry>(
                """
                SELECT * FROM Outbox
                WHERE Status IN (?, ?)
                  AND RecordId NOT IN (SELECT RecordId FROM Outbox WHERE Status IN (?, ?))
                ORDER BY Seq
                LIMIT ?
                """,
                (int)OutboxStatus.Pending, (int)OutboxStatus.Failed,
                (int)OutboxStatus.Conflict, (int)OutboxStatus.Rejected,
                _options.PushBatchSize);

            if (batch.Count == 0) return;

            var request = new SyncPushRequest(context.DeviceId, context.AppVersion, batch.Select(ToChange).ToList());

            SyncPushResponse response;
            try
            {
                response = await transport.PushAsync(request, ct);
            }
            catch (SyncTransportException ex) when (ex.Kind is SyncFailureKind.Transient or SyncFailureKind.Refused)
            {
                await MarkFailedAsync(batch, ex.Message);
                throw;
            }

            var results = response.Results.ToDictionary(r => r.ChangeId);
            await db.Connection.RunInTransactionAsync(c =>
            {
                foreach (var entry in batch)
                {
                    if (!results.TryGetValue(entry.ChangeId, out var result)) continue; // stays pending; resent next run
                    var local = LocalSyncRegistry.Find(entry.EntityType);
                    if (local is null) continue;

                    switch (result.Status)
                    {
                        case SyncChangeStatus.Applied:
                        case SyncChangeStatus.Duplicate:
                            c.Delete(entry);
                            if (result.ServerData is not null)
                                LocalApplier.ApplyServerRow(c, local, result.ServerData, now);
                            else
                                LocalApplier.MarkPushed(c, local, entry.RecordId, result.RowVersion, now);
                            break;

                        case SyncChangeStatus.Conflict:
                        case SyncChangeStatus.Rejected:
                            var conflict = result.Status == SyncChangeStatus.Conflict;
                            entry.Status = conflict ? OutboxStatus.Conflict : OutboxStatus.Rejected;
                            entry.LastError = result.Error;
                            entry.ServerData = result.ServerData?.ToJsonString(LocalJson.Options);
                            entry.ConflictId = result.ConflictId ?? entry.ConflictId;
                            entry.Attempts++;
                            c.Update(entry);
                            LocalApplier.SetRowStatus(c, local, entry.RecordId, conflict ? SyncStatus.Conflict : SyncStatus.Error);
                            break;
                    }
                    changedTypes.Add(entry.EntityType);
                }
            });

            if (batch.Count < _options.PushBatchSize) return;
        }
    }

    private async Task MarkFailedAsync(List<OutboxEntry> batch, string error)
    {
        var now = Now;
        await db.Connection.RunInTransactionAsync(c =>
        {
            foreach (var e in batch)
            {
                e.Attempts++;
                e.Status = OutboxStatus.Failed;
                e.LastError = error;
                e.NextAttemptAt = now + Backoff(e.Attempts);
                c.Update(e);
            }
        });
    }

    /// <summary>FR-SYN-12: exponential back-off 1 s → 2 s → 4 s … capped at 5 minutes.</summary>
    public TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaxBackoff.TotalSeconds));
    }

    private static SyncChange ToChange(OutboxEntry e) => new(
        e.ChangeId,
        e.EntityType,
        e.RecordId,
        (SyncOperation)e.Operation,
        e.BaseRowVersion,
        LocalJson.Fields(e.ChangedFields),
        LocalJson.Parse(e.Payload),
        new DateTimeOffset(DateTime.SpecifyKind(e.ClientTimestamp, DateTimeKind.Utc)));

    // =====================================================================
    // Pull
    // =====================================================================

    private async Task PullAsync(HashSet<string> changedTypes, CancellationToken ct)
    {
        var checkpoint = (await db.Connection.FindAsync<SyncCheckpoint>(CheckpointScope))?.Value ?? 0;
        var resets = 0;
        if (checkpoint == 0 && _options.UseSnapshot) checkpoint = await ApplySnapshotAsync(changedTypes, ct);

        while (true)
        {
            var page = await transport.PullAsync(checkpoint, _options.PullPageSize, ct);

            if (page.ResetRequired)
            {
                // FR-SYN-24: our checkpoint is too old (or the server was restored). Start again from zero,
                // keeping only rows with unsynced local changes.
                if (++resets > 1) throw new InvalidOperationException("The server asked for a reset twice in a row.");
                await ResetLocalAsync();
                checkpoint = _options.UseSnapshot ? await ApplySnapshotAsync(changedTypes, ct) : 0;
                continue;
            }

            await ApplyPageAsync(page, changedTypes);

            checkpoint = page.Checkpoint;
            if (!page.HasMore) return;
        }
    }

    /// <summary>Applies one page of server rows and its checkpoint in one transaction (FR-SYN-21).</summary>
    private async Task ApplyPageAsync(SyncPullResponse page, HashSet<string> changedTypes)
    {
        var now = Now;
        await db.Connection.RunInTransactionAsync(c =>
        {
            foreach (var entry in LocalSyncRegistry.All.OrderBy(e => e.Order))
            {
                if (!page.Entities.TryGetValue(entry.Name, out var rows)) continue;
                foreach (var json in rows) LocalApplier.ApplyServerRow(c, entry, json, now);
                if (rows.Count > 0) changedTypes.Add(entry.Name);
            }

            // FR-DYN-31: refresh the DynamicData snapshot of records whose values arrived.
            if (page.Entities.TryGetValue(SyncEntityNames.FieldValue, out var values) && values.Count > 0)
            {
                Forms.DynamicSnapshots.RebuildAll(c, values.Select(v =>
                    (v["entityType"]!.GetValue<string>(), v["recordId"]!.GetValue<Guid>())));
            }

            // FR-PAT-03: keep the patient search index in step with pulled patients and their dynamic values.
            var touchedPatients = new List<Guid>();
            if (page.Entities.TryGetValue(SyncEntityNames.Patient, out var patients))
                touchedPatients.AddRange(patients.Select(p => p["id"]!.GetValue<Guid>()));
            if (page.Entities.TryGetValue(SyncEntityNames.FieldValue, out var fieldValues))
                touchedPatients.AddRange(fieldValues
                    .Where(v => v["entityType"]!.GetValue<string>() == SyncEntityNames.Patient)
                    .Select(v => v["recordId"]!.GetValue<Guid>()));
            touchedPatients.AddRange(page.Tombstones.Where(t => t.EntityType == SyncEntityNames.Patient).Select(t => t.RecordId));

            foreach (var tombstone in page.Tombstones)
            {
                var entry = LocalSyncRegistry.Find(tombstone.EntityType);
                if (entry is null) continue;
                LocalApplier.ApplyTombstone(c, entry, tombstone, now);
                changedTypes.Add(entry.Name);
            }

            if (touchedPatients.Count > 0) Clinical.PatientSearchIndex.Reindex(c, touchedPatients);

            // FR-SYN-21: the checkpoint commits with the page, so an interrupted pull resumes here.
            c.InsertOrReplace(new SyncCheckpoint { Scope = CheckpointScope, Value = page.Checkpoint, UpdatedAt = now });
        });
    }

    /// <summary>
    /// FR-SYN-23: a device starting from nothing downloads one compressed snapshot (the same pages, as NDJSON) instead
    /// of paging through the API, then continues with incremental pulls from the snapshot's checkpoint.
    /// </summary>
    private async Task<long> ApplySnapshotAsync(HashSet<string> changedTypes, CancellationToken ct)
    {
        await using var stream = await transport.SnapshotAsync(ct);
        if (stream is null) return 0;
        using var reader = new StreamReader(stream);
        long checkpoint = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var page = System.Text.Json.JsonSerializer.Deserialize<SyncPullResponse>(line, LocalJson.Options)
                       ?? throw new InvalidOperationException("The snapshot is damaged.");
            await ApplyPageAsync(page, changedTypes);
            checkpoint = page.Checkpoint;
        }
        return checkpoint;
    }

    /// <summary>FR-SYN-30/31: tell the server which conflicts were settled (after their resolving changes were pushed).</summary>
    private async Task ReportResolutionsAsync(CancellationToken ct)
    {
        var pending = await db.Connection.Table<PendingConflictResolution>().OrderBy(r => r.Seq).ToListAsync();
        if (pending.Count == 0) return;
        await transport.ReportResolutionsAsync(pending.Select(r => new ConflictResolutionReport(r.ConflictId, r.Resolution,
            LocalJson.Parse(r.ValuesJson), new DateTimeOffset(DateTime.SpecifyKind(r.ResolvedAt, DateTimeKind.Utc)))).ToList(), ct);
        foreach (var r in pending) await db.Connection.DeleteAsync<PendingConflictResolution>(r.Seq);
    }

    private async Task ResetLocalAsync()
    {
        await db.Connection.RunInTransactionAsync(c =>
        {
            foreach (var entry in LocalSyncRegistry.All)
            {
                var table = c.GetMapping(entry.LocalType).TableName;
                c.Execute($"DELETE FROM \"{table}\" WHERE Id NOT IN (SELECT RecordId FROM Outbox)");
            }
            c.Execute("DELETE FROM SyncCheckpoints");
        });
    }

    // =====================================================================
    // Number ranges (FR-OFF-22)
    // =====================================================================

    private async Task TopUpNumbersAsync(CancellationToken ct)
    {
        foreach (var kind in _options.LeaseKinds)
        {
            var remaining = await NumberSource.RemainingAsync(db, kind);
            if (remaining >= _options.LeaseSize * _options.TopUpThreshold) continue;

            var lease = await transport.LeaseNumbersAsync(kind, _options.LeaseSize, ct);
            await db.Connection.InsertOrReplaceAsync(new LeasedRange
            {
                Id = lease.Id,
                Kind = lease.Kind,
                Start = lease.Start,
                End = lease.End,
                Next = lease.Start,
            });
        }
    }

    // =====================================================================

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task PublishAsync(SyncPhase phase, string? message)
    {
        var counts = await db.Connection.QueryAsync<StatusCount>("SELECT Status, COUNT(*) AS Count FROM Outbox GROUP BY Status");
        int Count(OutboxStatus s) => counts.FirstOrDefault(c => c.Status == (int)s)?.Count ?? 0;

        var last = await db.GetMetaAsync(LastSuccessKey);
        DateTime? lastSuccess = DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

        State = new SyncState(phase, Count(OutboxStatus.Pending), Count(OutboxStatus.Failed),
            Count(OutboxStatus.Conflict), Count(OutboxStatus.Rejected), lastSuccess, message);
        StateChanged?.Invoke(this, State);
    }

    private sealed class StatusCount
    {
        public int Status { get; set; }
        public int Count { get; set; }
    }
}

/// <summary>Hands out leased numbers offline (FR-OFF-22). Throws when the device has run out.</summary>
public static class NumberSource
{
    public static async Task<long> NextAsync(LocalDatabase db, string kind)
    {
        long value = 0;
        await db.Connection.RunInTransactionAsync(c =>
        {
            var range = c.Query<LeasedRange>(
                "SELECT * FROM NumberRanges WHERE Kind = ? AND Next <= End ORDER BY Start LIMIT 1", kind).FirstOrDefault()
                ?? throw new InvalidOperationException($"No {kind} numbers left on this device. Connect to the internet to get more.");
            value = range.Next;
            range.Next++;
            c.Update(range);
        });
        return value;
    }

    public static Task<long> RemainingAsync(LocalDatabase db, string kind) =>
        db.Connection.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(End - Next + 1), 0) FROM NumberRanges WHERE Kind = ? AND Next <= End", kind);
}
