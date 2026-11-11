using System.Net;
using System.Net.Http.Headers;
using Azure.Messaging.ServiceBus;
using WebhookProcessor.Api;
using WebhookProcessor.Core;
using WebhookProcessor.Tests.Integration;

namespace WebhookProcessor.Tests.EndToEnd;

/// <summary>
/// Signed HTTP requests in, database rows out, through the real broker. These tests need Docker and are
/// tagged so a quick local run can skip them: dotnet test -- --filter-not-trait "Category=EndToEnd"
/// </summary>
[Trait("Category", "EndToEnd")]
[Collection(PostgresCollection.Name)]
public sealed class PipelineTests(PipelineFixture pipeline) : IClassFixture<PipelineFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpStatusCode> PostSignedAsync(byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/payments")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Headers.Add(PaymentWebhookEndpoint.TimestampHeader, timestamp);
        request.Headers.Add(PaymentWebhookEndpoint.SignatureHeader, WebhookSignature.Compute(PipelineFixture.SigningSecret, timestamp, body));

        using var response = await pipeline.Api.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>
    /// Waits until the worker has taken every message off the queue, so the final state can be checked.
    /// Peeking also sees messages that are locked by the worker but not yet completed.
    /// </summary>
    private async Task WaitForQueueToDrainAsync()
    {
        await using var receiver = pipeline.ServiceBus.CreateReceiver(PipelineFixture.QueueName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);

        while (await receiver.PeekMessageAsync(cancellationToken: timeout.Token) is not null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }

    [Fact]
    public async Task Same_event_sent_three_times_changes_the_payment_once()
    {
        var e = TestEvents.Create(amount: 1500);
        var body = TestEvents.ToJson(e);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, await PostSignedAsync(body));
        }

        await WaitForQueueToDrainAsync();

        Assert.Equal(new PaymentRow(e.PaymentId, "captured", 1500, "EUR"), await pipeline.Postgres.GetPaymentAsync(e.PaymentId));
        Assert.Equal(1, await pipeline.Postgres.CountEventsAsync(e.PaymentId));
    }

    [Fact]
    public async Task Ten_parallel_deliveries_through_the_broker_change_the_payment_once()
    {
        // Ten concurrent HTTP requests, then a worker with five concurrent message handlers.
        var e = TestEvents.Create(amount: 1500);
        var body = TestEvents.ToJson(e);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostSignedAsync(body)));

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Accepted, status));
        await WaitForQueueToDrainAsync();
        Assert.Equal(1500, (await pipeline.Postgres.GetPaymentAsync(e.PaymentId))!.CapturedAmount);
        Assert.Equal(1, await pipeline.Postgres.CountEventsAsync(e.PaymentId));
    }

    [Fact]
    public async Task Poison_message_goes_to_the_dead_letter_queue_and_does_not_block_the_queue()
    {
        // Sent straight to the queue, past the API, because anything can end up there.
        var poisonId = TestEvents.NewId("poison");
        await using (var sender = pipeline.ServiceBus.CreateSender(PipelineFixture.QueueName))
        {
            await sender.SendMessageAsync(new ServiceBusMessage(TestEvents.Utf8("{ not json")) { MessageId = poisonId }, Ct);
        }

        // A valid event sent after the poison one is still processed.
        var e = TestEvents.Create(amount: 1500);
        Assert.Equal(HttpStatusCode.Accepted, await PostSignedAsync(TestEvents.ToJson(e)));

        var deadLettered = await ReceiveFromDeadLetterQueueAsync(poisonId);
        Assert.Equal("PermanentFailure", deadLettered.DeadLetterReason);
        Assert.Equal(1, deadLettered.DeliveryCount);

        await WaitForQueueToDrainAsync();
        Assert.Equal(1500, (await pipeline.Postgres.GetPaymentAsync(e.PaymentId))!.CapturedAmount);
    }

    private async Task<ServiceBusReceivedMessage> ReceiveFromDeadLetterQueueAsync(string messageId)
    {
        // Receive-and-delete: the test only reads the dead-letter queue, so it needs no lock to settle.
        await using var receiver = pipeline.ServiceBus.CreateReceiver(
            PipelineFixture.QueueName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), Ct);
            if (message?.MessageId == messageId)
            {
                return message;
            }
        }

        throw new TimeoutException($"Message {messageId} did not reach the dead-letter queue within {Timeout}.");
    }
}
