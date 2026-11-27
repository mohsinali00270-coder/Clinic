using Clinic.Application.Clinical;
using Microsoft.Extensions.Logging;

namespace Clinic.Infrastructure.Messaging;

/// <summary>
/// Development / test sender: records the message in the log and reports success. Swap for an SMS gateway, the
/// WhatsApp Business API or SMTP adapter in production (same <see cref="IMessageSender"/> contract).
/// Numbers starting with "+920000" fail, so the failure path can be exercised.
/// </summary>
public sealed class LoggingMessageSender(ILogger<LoggingMessageSender> logger) : IMessageSender
{
    public Task<SendResult> SendAsync(string channel, string to, string body, CancellationToken ct)
    {
        if (to.StartsWith("+920000", StringComparison.Ordinal))
            return Task.FromResult(new SendResult(false, null, "Number not reachable (test).", Permanent: true));
        logger.LogInformation("[{Channel}] to {To}: {Body}", channel, to, body);
        return Task.FromResult(new SendResult(true, $"dev-{Guid.NewGuid():N}"[..16], null));
    }
}
