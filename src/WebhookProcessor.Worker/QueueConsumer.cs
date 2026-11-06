using Azure.Messaging.ServiceBus;

namespace WebhookProcessor.Worker;

public sealed class QueueConsumer : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
    ];

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

    public bool IsProcessing => _processor.IsProcessing;

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
        catch (PermanentEventException ex)
        {
            _logger.LogError("Dead-lettering message that can never succeed: {Reason}", ex.Message);
            await args.DeadLetterMessageAsync(
                message,
                deadLetterReason: "PermanentFailure",
                deadLetterErrorDescription: ex.Message,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            var delay = GetRetryDelay(message.DeliveryCount);
            _logger.LogWarning(
                ex,
                "Delivery {DeliveryCount} failed, abandoning after {DelaySeconds}s",
                message.DeliveryCount,
                delay.TotalSeconds);

            await WaitAsync(delay, args.CancellationToken);

            // The broker counts this delivery and dead-letters the message after MaxDeliveryCount.
            await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
    }

    /// <summary>
    /// The broker redelivers an abandoned message immediately, so the pause happens before abandoning.
    /// Every delay must stay well below the queue lock duration. There is no pause after the last
    /// delivery, because the message goes straight to the dead-letter queue.
    /// </summary>
    private static TimeSpan GetRetryDelay(int deliveryCount) =>
        deliveryCount >= 1 && deliveryCount <= RetryDelays.Length ? RetryDelays[deliveryCount - 1] : TimeSpan.Zero;

    private static async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: give the message back right away instead of holding its lock.
        }
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Service Bus error from {ErrorSource}", args.ErrorSource);
        return Task.CompletedTask;
    }
}
