using Clinic.Application.Sync;

namespace Clinic.Api.Infrastructure;

/// <summary>FR-SYN-24: compacts the ChangeLog once a day (Sync:ChangeLogRetentionDays, default 90).</summary>
public sealed class MaintenanceWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var days = config.GetValue("Sync:ChangeLogRetentionDays", 90);
                foreach (var r in await scope.ServiceProvider.GetRequiredService<ChangeLogRetention>().RunAsync(days, stoppingToken))
                    if (r.Superseded + r.Tombstones > 0)
                        logger.LogInformation("ChangeLog compaction for {Tenant}: {Superseded} superseded, {Tombstones} delete markers removed.", r.TenantId, r.Superseded, r.Tombstones);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "ChangeLog compaction failed; will retry tomorrow."); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
