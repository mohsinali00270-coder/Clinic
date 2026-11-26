using Clinic.Shared.Scheduling;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Clinic.Client.Core.Sync;

/// <summary>
/// FR-SYN-03: listens for the server's "changes available" event and asks for a sync, so queues and
/// appointments refresh across devices within seconds. Offline it simply stays disconnected and retries.
/// </summary>
public sealed class SyncNudgeClient : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public SyncNudgeClient(Uri serverBase, Func<Task<string?>> accessToken, Action onChanges,
        Func<HttpMessageHandler>? handlerFactory = null, HttpTransportType? transports = null)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(serverBase, SchedulingRoutes.Hub.TrimStart('/')), o =>
            {
                o.AccessTokenProvider = accessToken;
                if (handlerFactory is not null) o.HttpMessageHandlerFactory = _ => handlerFactory();
                if (transports is { } t) o.Transports = t;
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)])
            .Build();

        _connection.On<List<Guid>>(HubEvents.ChangesAvailable, _ => onChanges());
        // After a reconnect we may have missed nudges: sync once.
        _connection.Reconnected += _ => { onChanges(); return Task.CompletedTask; };
    }

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>Connects; returns false instead of throwing when the server is unreachable (offline).</summary>
    public async Task<bool> TryStartAsync(CancellationToken ct = default)
    {
        if (_connection.State != HubConnectionState.Disconnected) return IsConnected;
        try
        {
            await _connection.StartAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
