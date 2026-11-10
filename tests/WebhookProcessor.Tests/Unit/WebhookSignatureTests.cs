using WebhookProcessor.Core;

namespace WebhookProcessor.Tests.Unit;

public sealed class WebhookSignatureTests
{
    private const string Secret = "test-signing-secret";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly string NowTimestamp = Now.ToUnixTimeSeconds().ToString();
    private static readonly byte[] Body = TestEvents.Utf8("""{"eventId":"evt-1"}""");

    [Fact]
    public void Compute_matches_a_known_value()
    {
        // Pins the exact format, so a change that breaks existing senders fails here.
        // Expected value computed independently with: printf '1700000000.{}' | openssl dgst -sha256 -hmac secret
        var signature = WebhookSignature.Compute("secret", "1700000000", TestEvents.Utf8("{}"));

        Assert.Equal("sha256=b8569b78799ff9e3cbff0fc2d63a33a2b57f3282abd07c37ae5e8e7d79a5f163", signature);
    }

    [Fact]
    public void Accepts_a_correct_signature()
    {
        var signature = WebhookSignature.Compute(Secret, NowTimestamp, Body);

        Assert.True(WebhookSignature.IsValid(Secret, NowTimestamp, signature, Body, Now, Tolerance));
    }

    [Fact]
    public void Rejects_a_changed_body()
    {
        var signature = WebhookSignature.Compute(Secret, NowTimestamp, Body);
        var changed = TestEvents.Utf8("""{"eventId":"evt-2"}""");

        Assert.False(WebhookSignature.IsValid(Secret, NowTimestamp, signature, changed, Now, Tolerance));
    }

    [Fact]
    public void Rejects_a_signature_made_with_another_secret()
    {
        var signature = WebhookSignature.Compute("another-secret", NowTimestamp, Body);

        Assert.False(WebhookSignature.IsValid(Secret, NowTimestamp, signature, Body, Now, Tolerance));
    }

    [Fact]
    public void Rejects_a_changed_timestamp_with_the_old_signature()
    {
        // A replayed request with a fresh timestamp must fail, because the timestamp is signed too.
        var oldTimestamp = Now.AddHours(-1).ToUnixTimeSeconds().ToString();
        var oldSignature = WebhookSignature.Compute(Secret, oldTimestamp, Body);

        Assert.False(WebhookSignature.IsValid(Secret, NowTimestamp, oldSignature, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Rejects_a_timestamp_outside_the_tolerance(int offsetSeconds)
    {
        var timestamp = Now.AddSeconds(offsetSeconds).ToUnixTimeSeconds().ToString();
        var signature = WebhookSignature.Compute(Secret, timestamp, Body);

        Assert.False(WebhookSignature.IsValid(Secret, timestamp, signature, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public void Accepts_a_timestamp_at_the_edge_of_the_tolerance(int offsetSeconds)
    {
        var timestamp = Now.AddSeconds(offsetSeconds).ToUnixTimeSeconds().ToString();
        var signature = WebhookSignature.Compute(Secret, timestamp, Body);

        Assert.True(WebhookSignature.IsValid(Secret, timestamp, signature, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1700000000")]
    [InlineData("1700000000.5")]
    public void Rejects_a_missing_or_malformed_timestamp(string? timestamp)
    {
        var signature = WebhookSignature.Compute(Secret, timestamp ?? "", Body);

        Assert.False(WebhookSignature.IsValid(Secret, timestamp, signature, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("not-a-signature")]
    public void Rejects_a_missing_or_malformed_signature(string? signature)
    {
        Assert.False(WebhookSignature.IsValid(Secret, NowTimestamp, signature, Body, Now, Tolerance));
    }

    [Fact]
    public void Rejects_an_upper_case_hex_signature()
    {
        // The comparison is byte for byte. Documenting it here means a change is a deliberate decision.
        var signature = WebhookSignature.Compute(Secret, NowTimestamp, Body);
        var upper = "sha256=" + signature["sha256=".Length..].ToUpperInvariant();

        Assert.False(WebhookSignature.IsValid(Secret, NowTimestamp, upper, Body, Now, Tolerance));
    }
}
