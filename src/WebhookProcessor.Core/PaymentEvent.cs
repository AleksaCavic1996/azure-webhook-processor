using System.Text.Json;

namespace WebhookProcessor.Core;

/// <summary>A payment event as sent by the webhook provider. Amount is in minor units (for example cents).</summary>
public sealed record PaymentEvent(
    string EventId,
    string Type,
    string PaymentId,
    long Amount,
    string Currency,
    DateTimeOffset OccurredAt);

public static class PaymentEventTypes
{
    public const string Authorized = "payment.authorized";
    public const string Captured = "payment.captured";
    public const string Failed = "payment.failed";

    public static bool IsKnown(string? type) => type is Authorized or Captured or Failed;
}

public static class PaymentEventParser
{
    private const int MaxIdLength = 200;

    /// <summary>
    /// Parses and validates a raw event body. The API uses it to reject bad input, and the worker
    /// uses it again because anything can end up on the queue, not only what the API accepted.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> json, out PaymentEvent? paymentEvent, out IReadOnlyList<string> errors)
    {
        paymentEvent = null;

        PaymentEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PaymentEvent>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            errors = ["Body is not valid JSON for a payment event."];
            return false;
        }

        if (parsed is null)
        {
            errors = ["Body is empty."];
            return false;
        }

        errors = Validate(parsed);
        if (errors.Count > 0)
        {
            return false;
        }

        paymentEvent = parsed;
        return true;
    }

    private static List<string> Validate(PaymentEvent e)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(e.EventId) || e.EventId.Length > MaxIdLength)
        {
            errors.Add($"eventId is required, up to {MaxIdLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(e.PaymentId) || e.PaymentId.Length > MaxIdLength)
        {
            errors.Add($"paymentId is required, up to {MaxIdLength} characters.");
        }

        if (!PaymentEventTypes.IsKnown(e.Type))
        {
            errors.Add("type is not a known event type.");
        }

        if (e.Amount < 0 || (e.Type == PaymentEventTypes.Captured && e.Amount == 0))
        {
            errors.Add("amount must not be negative, and a captured amount must be positive.");
        }

        if (e.Currency is not { Length: 3 } || !e.Currency.All(char.IsAsciiLetterUpper))
        {
            errors.Add("currency must be a 3-letter upper case code.");
        }

        if (e.OccurredAt == default)
        {
            errors.Add("occurredAt is required.");
        }

        return errors;
    }
}
