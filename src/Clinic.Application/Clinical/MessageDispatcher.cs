using Clinic.Application.Abstractions;
using Clinic.Domain.Clinical;
using Clinic.Shared.Clinical;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Clinic.Application.Clinical;

public sealed record SendResult(bool Success, string? ProviderMessageId, string? Error, bool Permanent = false);

/// <summary>
/// FR-COM-03 delivery: one adapter per provider (SMS gateway, WhatsApp Business API, SMTP). The development adapter
/// only logs. Providers are configured per deployment; the queue and status flow do not change.
/// </summary>
public interface IMessageSender
{
    Task<SendResult> SendAsync(string channel, string to, string body, CancellationToken ct);
}

/// <summary>
/// Sends messages queued by devices (after they sync) and records the outcome on the message, which syncs back so the
/// patient timeline shows it. Opted-out patients are skipped. Failures are retried up to <see cref="MaxAttempts"/> times.
/// </summary>
public sealed class MessageDispatcher(IAppDbContext db, IMessageSender sender, ILogger<MessageDispatcher> logger)
{
    public const int MaxAttempts = 3;

    public async Task<int> DispatchAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var queued = await db.OutboundMessages.IgnoreQueryFilters()
            .Where(m => m.Status == MessageStatus.Queued && !m.IsDeleted)
            .OrderBy(m => m.QueuedAt).Take(batchSize)
            .Select(m => new { m.Id, m.TenantId }).ToListAsync(ct);

        var handled = 0;
        foreach (var tenantGroup in queued.GroupBy(q => q.TenantId))
        {
            using var scope = db.BeginTenantScope(tenantGroup.Key);
            foreach (var item in tenantGroup)
            {
                var m = await db.OutboundMessages.FirstOrDefaultAsync(x => x.Id == item.Id, ct);
                if (m is null || m.Status != MessageStatus.Queued) continue;

                var pref = await db.ContactPreferences.FirstOrDefaultAsync(p => p.PatientId == m.PatientId, ct);
                if (pref?.OptOut == true)
                {
                    m.Status = MessageStatus.Skipped;
                    m.Error = "The patient has opted out of messages.";
                }
                else
                {
                    m.Attempts++;
                    SendResult result;
                    try { result = await sender.SendAsync(m.Channel, m.To, m.Body, ct); }
                    catch (Exception ex) { result = new SendResult(false, null, ex.Message); }

                    if (result.Success)
                    {
                        m.Status = MessageStatus.Sent;
                        m.SentAt = DateTime.UtcNow;
                        m.ProviderMessageId = result.ProviderMessageId;
                        m.Error = null;
                    }
                    else
                    {
                        m.Error = result.Error;
                        if (result.Permanent || m.Attempts >= MaxAttempts) m.Status = MessageStatus.Failed;
                        logger.LogWarning("Message {Id} to {Channel} failed (attempt {Attempt}): {Error}", m.Id, m.Channel, m.Attempts, result.Error);
                    }
                }
                await db.SaveChangesAsync(ct);
                handled++;
            }
        }
        return handled;
    }
}
