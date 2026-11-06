using Azure.Messaging.ServiceBus;
using WebhookProcessor.Core;

namespace WebhookProcessor.Api;

public sealed record WebhookSettings(string SigningSecret, TimeSpan TimestampTolerance);

public static class PaymentWebhookEndpoint
{
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string SignatureHeader = "X-Webhook-Signature";

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        WebhookSettings settings,
        ServiceBusSender sender,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(PaymentWebhookEndpoint));

        // The signature covers the exact bytes received, so read them before any JSON parsing.
        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            body = buffer.ToArray();
        }
        catch (BadHttpRequestException ex)
        {
            return Results.StatusCode(ex.StatusCode);
        }

        var signatureIsValid = WebhookSignature.IsValid(
            settings.SigningSecret,
            request.Headers[TimestampHeader],
            request.Headers[SignatureHeader],
            body,
            clock.GetUtcNow(),
            settings.TimestampTolerance);

        if (!signatureIsValid)
        {
            logger.LogWarning("Webhook rejected: invalid signature or timestamp");
            return Results.Unauthorized();
        }

        if (!PaymentEventParser.TryParse(body, out var paymentEvent, out var errors))
        {
            logger.LogWarning("Webhook rejected: invalid payload, {ErrorCount} error(s)", errors.Count);
            return Results.BadRequest(new { errors });
        }

        var message = new ServiceBusMessage(body)
        {
            MessageId = paymentEvent!.EventId,
            ContentType = "application/json",
            Subject = paymentEvent.Type,
        };

        try
        {
            await sender.SendMessageAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 202 is only safe once the broker owns the event. A 503 makes the provider retry.
            logger.LogError(ex, "Could not enqueue event {PaymentEventId}", paymentEvent.EventId);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation(
            "Enqueued event {PaymentEventId} of type {EventType} for payment {PaymentId}",
            paymentEvent.EventId,
            paymentEvent.Type,
            paymentEvent.PaymentId);

        return Results.Accepted(value: new { paymentEvent.EventId });
    }
}
