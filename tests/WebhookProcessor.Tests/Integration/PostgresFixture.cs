using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using WebhookProcessor.Worker;

namespace WebhookProcessor.Tests.Integration;

/// <summary>
/// Test classes that need the database join this collection. The container starts only when one of
/// them runs, so the unit and API tests work on a machine without Docker.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// One throwaway PostgreSQL container for all tests that use the database, with the real migrations applied.
/// Tests share it safely because every test uses its own fresh event and payment ids.
/// The image matches docker-compose.yml, so tests run against the same version as local development.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:18.6-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DatabaseMigrator.Migrate(ConnectionString, NullLogger.Instance);
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    public async Task<PaymentRow?> GetPaymentAsync(string paymentId)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<PaymentRow>(
            "SELECT payment_id AS PaymentId, status AS Status, captured_amount AS CapturedAmount, currency AS Currency FROM payments WHERE payment_id = @paymentId",
            new { paymentId });
    }

    public async Task<int> CountEventsAsync(string paymentId)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM processed_events WHERE payment_id = @paymentId",
            new { paymentId });
    }
}

public sealed record PaymentRow(string PaymentId, string Status, long CapturedAmount, string Currency);
