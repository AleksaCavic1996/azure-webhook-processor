using Azure.Messaging.ServiceBus;
using WebhookProcessor.Api;

var builder = WebApplication.CreateBuilder(args);

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
    RetryOptions = new ServiceBusRetryOptions { MaxRetries = 2, TryTimeout = TimeSpan.FromSeconds(5) },
}));
builder.Services.AddSingleton(services => services.GetRequiredService<ServiceBusClient>().CreateSender(queueName));

var app = builder.Build();

app.MapPost("/webhooks/payments", PaymentWebhookEndpoint.HandleAsync);

app.Run();
