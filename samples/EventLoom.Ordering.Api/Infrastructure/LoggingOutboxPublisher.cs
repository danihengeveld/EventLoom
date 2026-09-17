using EventLoom.EntityFrameworkCore;
using EventLoom.Hosting;

namespace EventLoom.Ordering.Api;

internal sealed class LoggingOutboxPublisher(ILogger<LoggingOutboxPublisher> logger) : IOutboxPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Published outbox message {MessageId} for tenant {TenantId} at offset {TenantOffset}.",
            message.MessageId,
            message.TenantId,
            message.TenantOffset);
        return Task.CompletedTask;
    }
}
