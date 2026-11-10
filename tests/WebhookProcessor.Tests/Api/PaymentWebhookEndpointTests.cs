using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Messaging.ServiceBus;
using WebhookProcessor.Api;
using WebhookProcessor.Core;

namespace WebhookProcessor.Tests.Api;

/// <summary>
/// A new factory per test keeps the recorded messages and the clock separate between tests.
/// </summary>
public sealed class PaymentWebhookEndpointTests : IAsyncDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public PaymentWebhookEndpointTests() => _client = _factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Now => _factory.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();

    private Task<HttpResponseMessage> PostAsync(byte[] body, string? timestamp, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/payments")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };

        if (timestamp is not null)
        {
            request.Headers.Add(PaymentWebhookEndpoint.TimestampHeader, timestamp);
        }

        if (signature is not null)
        {
            request.Headers.Add(PaymentWebhookEndpoint.SignatureHeader, signature);
        }

        return _client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> PostSignedAsync(byte[] body) =>
        PostAsync(body, Now, WebhookSignature.Compute(ApiFactory.SigningSecret, Now, body));

    [Fact]
    public async Task Valid_event_is_enqueued_and_accepted()
    {
        var paymentEvent = TestEvents.Create();
        var body = TestEvents.ToJson(paymentEvent);

        using var response = await PostSignedAsync(body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedBody>(Ct);
        Assert.Equal(paymentEvent.EventId, accepted?.EventId);

        var message = Assert.Single(_factory.Sender.Messages);
        Assert.Equal(paymentEvent.EventId, message.MessageId);
        Assert.Equal(paymentEvent.Type, message.Subject);
        Assert.Equal("application/json", message.ContentType);

        // The worker gets the exact bytes that were signed, not a re-serialized copy.
        Assert.Equal(body, message.Body.ToArray());
    }

    [Fact]
    public async Task Missing_signature_headers_are_rejected()
    {
        using var response = await PostAsync(TestEvents.ToJson(TestEvents.Create()), timestamp: null, signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Sender.Messages);
    }

    [Fact]
    public async Task Wrong_signature_is_rejected()
    {
        var body = TestEvents.ToJson(TestEvents.Create());

        using var response = await PostAsync(body, Now, WebhookSignature.Compute("wrong-secret", Now, body));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Sender.Messages);
    }

    [Fact]
    public async Task Body_changed_after_signing_is_rejected()
    {
        var signed = TestEvents.ToJson(TestEvents.Create(amount: 1500));
        var changed = TestEvents.ToJson(TestEvents.Create(amount: 999_999));

        using var response = await PostAsync(changed, Now, WebhookSignature.Compute(ApiFactory.SigningSecret, Now, signed));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Sender.Messages);
    }

    [Fact]
    public async Task Request_older_than_the_tolerance_is_rejected()
    {
        var body = TestEvents.ToJson(TestEvents.Create());
        var timestamp = Now;
        var signature = WebhookSignature.Compute(ApiFactory.SigningSecret, timestamp, body);

        // Replaying a captured request six minutes later. The default tolerance is five minutes.
        _factory.Clock.Advance(TimeSpan.FromMinutes(6));
        using var response = await PostAsync(body, timestamp, signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Sender.Messages);
    }

    [Fact]
    public async Task Signed_but_invalid_payload_is_rejected_with_the_reasons()
    {
        var body = TestEvents.ToJson(TestEvents.Create(currency: "eur"));

        using var response = await PostSignedAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ErrorsBody>(Ct);
        Assert.Contains(problem!.Errors, error => error.StartsWith("currency", StringComparison.Ordinal));
        Assert.Empty(_factory.Sender.Messages);
    }

    [Fact]
    public async Task Signature_is_checked_before_the_payload()
    {
        // An unsigned caller must not learn anything about validation rules.
        using var response = await PostAsync(TestEvents.Utf8("not json"), Now, "sha256=bad");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Broker_failure_returns_503_so_the_provider_retries()
    {
        _factory.Sender.FailWith = new ServiceBusException("broker down", ServiceBusFailureReason.ServiceCommunicationProblem);

        using var response = await PostSignedAsync(TestEvents.ToJson(TestEvents.Create()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Same_event_twice_is_enqueued_twice_and_left_to_the_worker()
    {
        // The API stays stateless. Deduplication is the worker's job, against the database.
        var body = TestEvents.ToJson(TestEvents.Create());

        using var first = await PostSignedAsync(body);
        using var second = await PostSignedAsync(body);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(2, _factory.Sender.Messages.Count);
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_the_broker()
    {
        using var response = await _client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record AcceptedBody(string EventId);

    private sealed record ErrorsBody(string[] Errors);
}
