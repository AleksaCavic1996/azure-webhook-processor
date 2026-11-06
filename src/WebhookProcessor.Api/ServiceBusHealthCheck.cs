using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WebhookProcessor.Api;

/// <summary>
/// Creating a batch on a new sender opens a send link without sending anything, and it needs only
/// the send permission the API already has. The long-lived sender cannot be used here: it caches
/// the link's size limit, so after the first call it would report healthy with the broker down.
/// </summary>
public sealed class ServiceBusHealthCheck(ServiceBusClient client, ServiceBusSender sender) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var probe = client.CreateSender(sender.EntityPath);
            using var batch = await probe.CreateMessageBatchAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Service Bus is not reachable.", ex);
        }
    }
}
