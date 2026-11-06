using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace WebhookProcessor.Worker;

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}

/// <summary>Reports whether the message loop is running. It does not prove the broker is reachable.</summary>
public sealed class QueueConsumerHealthCheck(QueueConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(consumer.IsProcessing
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The queue consumer is not running."));
}
