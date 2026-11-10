using WebhookProcessor.Core;

namespace WebhookProcessor.Tests.Unit;

public sealed class PaymentEventParserTests
{
    private const string ValidJson = """
        {"eventId":"evt-1","type":"payment.captured","paymentId":"pay-1","amount":1500,"currency":"EUR","occurredAt":"2026-11-10T12:00:00+01:00"}
        """;

    [Fact]
    public void Parses_a_valid_event()
    {
        Assert.True(PaymentEventParser.TryParse(TestEvents.Utf8(ValidJson), out var e, out var errors));

        Assert.Empty(errors);
        Assert.Equal(new PaymentEvent(
            "evt-1",
            PaymentEventTypes.Captured,
            "pay-1",
            1500,
            "EUR",
            new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero)), e);
    }

    [Theory]
    [InlineData(PaymentEventTypes.Authorized)]
    [InlineData(PaymentEventTypes.Captured)]
    [InlineData(PaymentEventTypes.Failed)]
    public void Accepts_every_known_type(string type)
    {
        var body = TestEvents.ToJson(TestEvents.Create(type: type));

        Assert.True(PaymentEventParser.TryParse(body, out _, out _));
    }

    [Theory]
    [InlineData(PaymentEventTypes.Authorized)]
    [InlineData(PaymentEventTypes.Failed)]
    public void Accepts_a_zero_amount_when_nothing_is_captured(string type)
    {
        var body = TestEvents.ToJson(TestEvents.Create(type: type, amount: 0));

        Assert.True(PaymentEventParser.TryParse(body, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"amount":"fifteen"}""")]
    public void Rejects_a_body_that_is_not_an_event(string body)
    {
        Assert.False(PaymentEventParser.TryParse(TestEvents.Utf8(body), out var e, out var errors));

        Assert.Null(e);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Rejects_a_json_null()
    {
        Assert.False(PaymentEventParser.TryParse(TestEvents.Utf8("null"), out _, out var errors));

        Assert.Equal(["Body is empty."], errors);
    }

    public static TheoryData<PaymentEvent, string> InvalidEvents => new()
    {
        { TestEvents.Create(eventId: ""), "eventId" },
        { TestEvents.Create(eventId: "   "), "eventId" },
        { TestEvents.Create(eventId: new string('e', 201)), "eventId" },
        { TestEvents.Create(paymentId: ""), "paymentId" },
        { TestEvents.Create(paymentId: new string('p', 201)), "paymentId" },
        { TestEvents.Create(type: "payment.refunded"), "type" },
        { TestEvents.Create(type: "PAYMENT.CAPTURED"), "type" },
        { TestEvents.Create(amount: -1), "amount" },
        { TestEvents.Create(type: PaymentEventTypes.Captured, amount: 0), "amount" },
        { TestEvents.Create(currency: "eur"), "currency" },
        { TestEvents.Create(currency: "EURO"), "currency" },
        { TestEvents.Create(currency: "E1R"), "currency" },
        { TestEvents.Create(occurredAt: default(DateTimeOffset)), "occurredAt" },
    };

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public void Rejects_an_invalid_field_and_names_it(PaymentEvent invalid, string field)
    {
        Assert.False(PaymentEventParser.TryParse(TestEvents.ToJson(invalid), out var e, out var errors));

        Assert.Null(e);
        Assert.Contains(errors, error => error.StartsWith(field, StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_ids_at_the_maximum_length()
    {
        var body = TestEvents.ToJson(TestEvents.Create(eventId: new string('e', 200), paymentId: new string('p', 200)));

        Assert.True(PaymentEventParser.TryParse(body, out _, out _));
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var body = TestEvents.Utf8("""{"eventId":"","type":"x","paymentId":"","amount":-5,"currency":"e"}""");

        Assert.False(PaymentEventParser.TryParse(body, out _, out var errors));

        Assert.Equal(6, errors.Count);
    }
}
