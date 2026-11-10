using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.ServiceBus;
using WebhookProcessor.Api;
using WebhookProcessor.Tests.Integration;
using WebhookProcessor.Worker;

namespace WebhookProcessor.Tests.EndToEnd;

/// <summary>
/// The whole pipeline: the real API and worker in memory, the Service Bus emulator and PostgreSQL in containers.
/// The emulator also starts a SQL Server container, so this is the slowest fixture (about a minute on first run).
/// </summary>
public sealed class PipelineFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const string QueueName = "payment-events";
    public const string SigningSecret = "e2e-signing-secret";

    // The same emulator version and queue definition as docker-compose.yml.
    private readonly ServiceBusContainer _serviceBus = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1")
        .WithAcceptLicenseAgreement(true)
        .WithConfig(Path.Combine(AppContext.BaseDirectory, "servicebus-config.json"))
        .Build();

    private WebApplicationFactory<WebhookSettings> _api = null!;
    private WebApplicationFactory<QueueConsumer> _worker = null!;

    public PostgresFixture Postgres => postgres;

    public HttpClient Api { get; private set; } = null!;

    public ServiceBusClient ServiceBus { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _serviceBus.StartAsync();
        var serviceBusConnection = _serviceBus.GetConnectionString();

        // "Testing" keeps appsettings.Development.json out, so its localhost addresses cannot win over these.
        _api = new WebApplicationFactory<WebhookSettings>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:ServiceBus", serviceBusConnection)
            .UseSetting("Webhook:SigningSecret", SigningSecret));

        _worker = new WebApplicationFactory<QueueConsumer>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:ServiceBus", serviceBusConnection)
            .UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)
            .UseSetting("Database:MigrateOnStartup", "true"));

        Api = _api.CreateClient();

        // Creating the server starts the worker's hosted queue consumer.
        _ = _worker.Server;

        ServiceBus = new ServiceBusClient(serviceBusConnection);
    }

    public async ValueTask DisposeAsync()
    {
        if (ServiceBus is not null)
        {
            await ServiceBus.DisposeAsync();
        }

        Api?.Dispose();

        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await _serviceBus.DisposeAsync();
    }
}
