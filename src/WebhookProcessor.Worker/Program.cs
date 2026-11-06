using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using WebhookProcessor.Worker;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "O";
});

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var serviceBus = builder.Configuration.GetConnectionString("ServiceBus")
    ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is not configured.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres));
builder.Services.AddSingleton(new ServiceBusClient(serviceBus));
builder.Services.AddSingleton<PaymentRepository>();
builder.Services.AddSingleton<EventProcessor>();
builder.Services.AddSingleton<QueueConsumer>();
builder.Services.AddHostedService(services => services.GetRequiredService<QueueConsumer>());

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(3))
    .AddCheck<QueueConsumerHealthCheck>("queue-consumer", tags: ["ready"]);

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    DatabaseMigrator.Migrate(postgres, app.Services.GetRequiredService<ILogger<Program>>());
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
