using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;
using WebhookProcessor.Worker;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "O";
});

// Traces, metrics and logs go to Application Insights through OpenTelemetry, but only where a
// connection string is configured (Azure). Locally and in tests nothing is collected or sent.
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    // Lets the Service Bus client emit spans, so the worker continues the trace the API started.
    AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

    // Adds one span per SQL command, so a trace shows the time spent in PostgreSQL.
    builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddNpgsql());

    // Health probes arrive every 30 seconds and would bury the real work.
    builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        options.Filter = context => !context.Request.Path.StartsWithSegments("/health"));
}

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

// On Azure the worker signs in to PostgreSQL and Service Bus with its managed identity, so no
// password or key exists anywhere. Without these settings it uses the password in the connection
// string and a Service Bus connection string, which is how local runs and the tests work.
var postgresUsesIdentity = builder.Configuration.GetValue<bool>("Postgres:UseManagedIdentity");
var serviceBusNamespace = builder.Configuration["ServiceBus:FullyQualifiedNamespace"];
var azureCredential = new DefaultAzureCredential();

var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgres);
if (postgresUsesIdentity)
{
    // The server accepts a Microsoft Entra access token in place of a password. Tokens expire,
    // so the data source fetches a fresh one in the background well before that happens.
    dataSourceBuilder.UsePeriodicPasswordProvider(
        async (_, cancellationToken) => await PostgresAccessToken.GetAsync(azureCredential, cancellationToken),
        successRefreshInterval: TimeSpan.FromMinutes(30),
        failureRefreshInterval: TimeSpan.FromSeconds(10));
}

builder.Services.AddSingleton(dataSourceBuilder.Build());
builder.Services.AddSingleton(string.IsNullOrEmpty(serviceBusNamespace)
    ? new ServiceBusClient(
        builder.Configuration.GetConnectionString("ServiceBus")
            ?? throw new InvalidOperationException(
                "Configure ServiceBus:FullyQualifiedNamespace or ConnectionStrings:ServiceBus."))
    : new ServiceBusClient(serviceBusNamespace, azureCredential));
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
    // The migration tool takes a plain connection string, so with identity sign-in it gets one
    // access token as the password. A token outlives the few seconds a migration takes.
    var migrationConnectionString = postgresUsesIdentity
        ? new NpgsqlConnectionStringBuilder(postgres)
        {
            Password = await PostgresAccessToken.GetAsync(azureCredential, CancellationToken.None),
        }.ConnectionString
        : postgres;

    DatabaseMigrator.Migrate(migrationConnectionString, app.Services.GetRequiredService<ILogger<Program>>());
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
