using WebhookProcessor.Core;

namespace WebhookProcessor.Worker;

/// <summary>An event that can never succeed, so retrying it is pointless.</summary>
public sealed class PermanentEventException(string message) : Exception(message);

/// <summary>Processing logic with no knowledge of the broker, which keeps it easy to test.</summary>
public sealed class EventProcessor(PaymentRepository repository, ILogger<EventProcessor> logger)
{
    public async Task ProcessAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (!PaymentEventParser.TryParse(body.Span, out var paymentEvent, out var errors))
        {
            throw new PermanentEventException(string.Join(" ", errors));
        }

        var outcome = await repository.ApplyAsync(paymentEvent!, cancellationToken);

        logger.LogInformation(
            "Event {PaymentEventId} of type {EventType} for payment {PaymentId}: {Outcome}",
            paymentEvent!.EventId,
            paymentEvent.Type,
            paymentEvent.PaymentId,
            outcome);
    }
}
