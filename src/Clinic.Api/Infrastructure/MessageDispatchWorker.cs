using Clinic.Application.Clinical;

namespace Clinic.Api.Infrastructure;

/// <summary>FR-COM-03: sends queued patient messages every few seconds (Messaging:PollSeconds).</summary>
public sealed class MessageDispatchWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<MessageDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Messaging:PollSeconds", 15)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MessageDispatcher>().DispatchAsync(ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Message dispatch failed; will retry."); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
