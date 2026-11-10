using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using WebhookProcessor.Api;

namespace WebhookProcessor.Tests.Api;

/// <summary>
/// Runs the real API in memory with the queue replaced by <see cref="RecordingSender"/>, so these tests need no Docker.
/// <see cref="WebhookSettings"/> only marks which assembly holds the entry point.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<WebhookSettings>
{
    public const string SigningSecret = "api-test-signing-secret";

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public RecordingSender Sender { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program reads these before the services are built. The client is created but never connects.
        builder.UseSetting("ConnectionStrings:ServiceBus",
            "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
        builder.UseSetting("Webhook:SigningSecret", SigningSecret);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ServiceBusSender>();
            services.AddSingleton<ServiceBusSender>(Sender);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>A sender that keeps what it was given instead of talking to a broker.</summary>
public sealed class RecordingSender : ServiceBusSender
{
    public ConcurrentQueue<ServiceBusMessage> Messages { get; } = new();

    /// <summary>When set, every send throws it, as the real client does when the broker is down.</summary>
    public Exception? FailWith { get; set; }

    public override string EntityPath => "payment-events";

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Messages.Enqueue(message);
        return Task.CompletedTask;
    }
}
