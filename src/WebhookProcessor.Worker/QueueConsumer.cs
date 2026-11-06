using Azure.Messaging.ServiceBus;

namespace WebhookProcessor.Worker;

public sealed class QueueConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusProcessor _processor;
    private readonly EventProcessor _eventProcessor;
    private readonly ILogger<QueueConsumer> _logger;

    public QueueConsumer(
        ServiceBusClient client,
        IConfiguration configuration,
        EventProcessor eventProcessor,
        ILogger<QueueConsumer> logger)
    {
        _eventProcessor = eventProcessor;
        _logger = logger;

        var queueName = configuration["ServiceBus:QueueName"]
            ?? throw new InvalidOperationException("ServiceBus:QueueName is not configured.");

        _processor = client.CreateProcessor(queueName, new ServiceBusProcessorOptions
        {
            // Settling by hand means a message is completed only after the database commit.
            AutoCompleteMessages = false,
            MaxConcurrentCalls = configuration.GetValue("ServiceBus:MaxConcurrentCalls", 5),
        });
        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += HandleErrorAsync;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _processor.StartProcessingAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _processor.StopProcessingAsync(cancellationToken);

    public ValueTask DisposeAsync() => _processor.DisposeAsync();

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["messageId"] = message.MessageId,
            ["deliveryCount"] = message.DeliveryCount,
        });

        try
        {
            await _eventProcessor.ProcessAsync(message.Body.ToMemory(), args.CancellationToken);

            // A crash right here redelivers the message, and the database then recognizes the duplicate.
            await args.CompleteMessageAsync(message, args.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Processing failed");
            await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Service Bus error from {ErrorSource}", args.ErrorSource);
        return Task.CompletedTask;
    }
}
