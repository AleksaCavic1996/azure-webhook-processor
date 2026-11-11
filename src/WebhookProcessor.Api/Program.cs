using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Instrumentation.AspNetCore;
using WebhookProcessor.Api;

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
    // Lets the Service Bus client emit spans, so a trace continues from the API into the worker.
    AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

    // Health probes arrive every 30 seconds and would bury the real requests.
    builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        options.Filter = context => !context.Request.Path.StartsWithSegments("/health"));
}

var serviceBus = builder.Configuration.GetConnectionString("ServiceBus")
    ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is not configured.");
var queueName = builder.Configuration["ServiceBus:QueueName"]
    ?? throw new InvalidOperationException("ServiceBus:QueueName is not configured.");
var signingSecret = builder.Configuration["Webhook:SigningSecret"]
    ?? throw new InvalidOperationException("Webhook:SigningSecret is not configured.");

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new WebhookSettings(
    signingSecret,
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Webhook:TimestampToleranceSeconds", 300))));

// Fail fast when the broker is down, so the provider gets a 503 quickly instead of a hanging request.
builder.Services.AddSingleton(new ServiceBusClient(serviceBus, new ServiceBusClientOptions
{
    RetryOptions = new ServiceBusRetryOptions { MaxRetries = 1, TryTimeout = TimeSpan.FromSeconds(4) },
}));
builder.Services.AddSingleton(services => services.GetRequiredService<ServiceBusClient>().CreateSender(queueName));

builder.Services.AddHealthChecks()
    .AddCheck<ServiceBusHealthCheck>("service-bus", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();

app.MapPost("/webhooks/payments", PaymentWebhookEndpoint.HandleAsync);
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
