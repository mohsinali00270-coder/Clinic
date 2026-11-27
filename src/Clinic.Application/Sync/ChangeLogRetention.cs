using Clinic.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Sync;

public sealed class RetentionOptions
{
    public const string Section = "Sync";

    /// <summary>FR-SYN-24: how long the ChangeLog keeps superseded rows and delete markers (default 90 days).</summary>
    public int ChangeLogRetentionDays { get; set; } = 90;
}

/// <summary>
/// FR-SYN-24 compaction. Older than the retention:
/// <list type="bullet">
/// <item>rows superseded by a newer change of the same record are removed (the newest row still brings any device up to date);</item>
/// <item>delete markers are removed too, and <c>PurgedThrough</c> moves past them, so a device whose checkpoint is older
/// is told to reset (it would otherwise miss those deletions) — after pushing its outbox, it re-downloads the snapshot.</item>
/// </list>
/// </summary>
public sealed class ChangeLogRetention(IAppDbContext db, TimeProvider clock)
{
    public sealed record Result(Guid TenantId, int Superseded, int Tombstones, long PurgedThrough);

    public async Task<IReadOnlyList<Result>> RunAsync(int retentionDays, CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, retentionDays));
        var results = new List<Result>();
        var tenants = await db.Tenants.IgnoreQueryFilters().Select(t => t.Id).ToListAsync(ct);
        foreach (var tenant in tenants)
        {
            using var scope = db.BeginTenantScope(tenant);
            var superseded = await db.ChangeLogs
                .Where(c => c.At < cutoff && db.ChangeLogs.Any(n => n.RecordId == c.RecordId && n.Sequence > c.Sequence))
                .ExecuteDeleteAsync(ct);

            var tombstones = db.ChangeLogs.Where(c => c.At < cutoff && c.Operation == "Delete");
            var maxTombstone = await tombstones.MaxAsync(c => (long?)c.Sequence, ct);
            var removed = maxTombstone is null ? 0 : await tombstones.ExecuteDeleteAsync(ct);

            var counter = await db.SyncCounters.FirstOrDefaultAsync(c => c.TenantId == tenant, ct);
            if (counter is not null && maxTombstone is { } max && max > counter.PurgedThrough)
            {
                counter.PurgedThrough = max;
                await db.SaveChangesAsync(ct);
            }
            results.Add(new Result(tenant, superseded, removed, counter?.PurgedThrough ?? 0));
        }
        return results;
    }
}

/// <summary>Sync telemetry: an activity per push/pull and counters for changes, conflicts and rejections.</summary>
public static class SyncTelemetry
{
    public const string SourceName = "Clinic.Sync";
    public const string MeterName = "Clinic.Sync";

    public static readonly System.Diagnostics.ActivitySource Source = new(SourceName);
    private static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName);

    public static readonly System.Diagnostics.Metrics.Counter<long> ChangesPushed = Meter.CreateCounter<long>("clinic.sync.changes_pushed", description: "Changes received from devices");
    public static readonly System.Diagnostics.Metrics.Counter<long> Conflicts = Meter.CreateCounter<long>("clinic.sync.conflicts", description: "Changes that hit a manual conflict");
    public static readonly System.Diagnostics.Metrics.Counter<long> Rejections = Meter.CreateCounter<long>("clinic.sync.rejections", description: "Changes refused by the server rules");
    public static readonly System.Diagnostics.Metrics.Counter<long> RowsPulled = Meter.CreateCounter<long>("clinic.sync.rows_pulled", description: "Rows sent to devices");
}
