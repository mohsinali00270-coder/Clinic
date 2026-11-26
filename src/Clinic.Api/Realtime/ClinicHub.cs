using System.Collections.Concurrent;
using Clinic.Application.Clinical;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Clinic.Api.Realtime;

/// <summary>
/// FR-SYN-03 / FR-APT-03: devices join their tenant and branch groups and are told when there is something to
/// pull, so queues and appointments refresh within seconds. Data itself always travels through sync.
/// </summary>
[Authorize]
public sealed class ClinicHub : Hub
{
    public static string TenantGroup(Guid tenant) => $"t:{tenant:N}";
    public static string BranchGroup(Guid tenant, Guid branch) => $"t:{tenant:N}:b:{branch:N}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        if (Guid.TryParse(user.FindFirst(ClinicClaimTypes.TenantId)?.Value, out var tenant))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenant));
            foreach (var claim in user.FindAll(ClinicClaimTypes.Branch))
                if (Guid.TryParse(claim.Value, out var branch))
                    await Groups.AddToGroupAsync(Context.ConnectionId, BranchGroup(tenant, branch));
        }
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Batches change notifications per tenant for a short moment (so the saving transaction has committed and
/// a burst of saves becomes one nudge), then tells the affected groups.
/// </summary>
public sealed class SignalRChangeNotifier(IHubContext<ClinicHub> hub, ILogger<SignalRChangeNotifier> logger) : IChangeNotifier
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private readonly ConcurrentDictionary<Guid, HashSet<Guid?>> _pending = new();

    public void Notify(Guid tenantId, IReadOnlyCollection<Guid?> branchIds)
    {
        var isFirst = false;
        _pending.AddOrUpdate(tenantId,
            _ => { isFirst = true; return [.. branchIds]; },
            (_, set) => { lock (set) set.UnionWith(branchIds); return set; });
        if (isFirst) _ = FlushLaterAsync(tenantId);
    }

    private async Task FlushLaterAsync(Guid tenantId)
    {
        try
        {
            await Task.Delay(Delay);
            if (!_pending.TryRemove(tenantId, out var branches)) return;
            List<Guid?> list;
            lock (branches) list = branches.ToList();

            var payload = list.Where(b => b is not null).Select(b => b!.Value).ToList();
            if (list.Any(b => b is null))
                await hub.Clients.Group(ClinicHub.TenantGroup(tenantId)).SendAsync(HubEvents.ChangesAvailable, payload);
            else
                foreach (var b in payload)
                    await hub.Clients.Group(ClinicHub.BranchGroup(tenantId, b)).SendAsync(HubEvents.ChangesAvailable, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send change notification for tenant {Tenant}", tenantId);
        }
    }
}
