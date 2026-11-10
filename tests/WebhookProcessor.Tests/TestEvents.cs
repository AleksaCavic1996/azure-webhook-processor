using System.Text;
using System.Text.Json;
using WebhookProcessor.Core;

namespace WebhookProcessor.Tests;

/// <summary>Builds event bodies for tests. Every call without an id gets a fresh one, so tests never share rows.</summary>
public static class TestEvents
{
    public static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    public static PaymentEvent Create(
        string? eventId = null,
        string? paymentId = null,
        string type = PaymentEventTypes.Captured,
        long amount = 1500,
        string currency = "EUR",
        DateTimeOffset? occurredAt = null) =>
        new(
            eventId ?? NewId("evt"),
            type,
            paymentId ?? NewId("pay"),
            amount,
            currency,
            occurredAt ?? new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public static byte[] ToJson(PaymentEvent e) => JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web);

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
