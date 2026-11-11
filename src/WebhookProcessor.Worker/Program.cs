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
