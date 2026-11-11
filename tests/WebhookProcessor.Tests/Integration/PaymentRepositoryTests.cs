using Microsoft.Extensions.Logging.Abstractions;
using WebhookProcessor.Core;
using WebhookProcessor.Worker;

namespace WebhookProcessor.Tests.Integration;

/// <summary>
/// The idempotency guarantees, checked against a real PostgreSQL. A fake database could not prove
/// these, because they rely on the primary key, ON CONFLICT and row locking.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PaymentRepositoryTests(PostgresFixture db)
{
    private readonly PaymentRepository _repository = new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_event_three_times_is_applied_once()
    {
        var e = TestEvents.Create(amount: 1500);

        var outcomes = new[]
        {
            await _repository.ApplyAsync(e, Ct),
            await _repository.ApplyAsync(e, Ct),
            await _repository.ApplyAsync(e, Ct),
        };

        Assert.Equal([ApplyOutcome.Applied, ApplyOutcome.Duplicate, ApplyOutcome.Duplicate], outcomes);
        var payment = await db.GetPaymentAsync(e.PaymentId);
        Assert.Equal(new PaymentRow(e.PaymentId, "captured", 1500, "EUR"), payment);
        Assert.Equal(1, await db.CountEventsAsync(e.PaymentId));
    }

    [Fact]
    public async Task Ten_parallel_deliveries_of_one_event_are_applied_once()
    {
        var e = TestEvents.Create(amount: 1500);

        // The gate releases all ten at once, so the inserts really race on separate connections.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = Enumerable.Range(0, 10)
            .Select(async _ =>
            {
                await gate.Task;
                return await _repository.ApplyAsync(e, Ct);
            })
            .ToArray();
        gate.SetResult();
        var outcomes = await Task.WhenAll(deliveries);

        Assert.Equal(1, outcomes.Count(o => o == ApplyOutcome.Applied));
        Assert.Equal(9, outcomes.Count(o => o == ApplyOutcome.Duplicate));
        Assert.Equal(1500, (await db.GetPaymentAsync(e.PaymentId))!.CapturedAmount);
        Assert.Equal(1, await db.CountEventsAsync(e.PaymentId));
    }

    [Fact]
    public async Task Ten_parallel_different_captures_of_one_payment_all_count()
    {
        // The opposite case: different events must not be lost to the same race.
        var paymentId = TestEvents.NewId("pay");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = Enumerable.Range(0, 10)
            .Select(async _ =>
            {
                await gate.Task;
                return await _repository.ApplyAsync(TestEvents.Create(paymentId: paymentId, amount: 100), Ct);
            })
            .ToArray();
        gate.SetResult();
        var outcomes = await Task.WhenAll(deliveries);

        Assert.All(outcomes, o => Assert.Equal(ApplyOutcome.Applied, o));
        Assert.Equal(1000, (await db.GetPaymentAsync(paymentId))!.CapturedAmount);
        Assert.Equal(10, await db.CountEventsAsync(paymentId));
    }

    [Fact]
    public async Task Redelivery_after_a_crash_before_completing_the_message_is_a_duplicate()
    {
        // The worker commits, then crashes before telling the broker. The broker redelivers the same message.
        var e = TestEvents.Create(amount: 1500);
        Assert.Equal(ApplyOutcome.Applied, await _repository.ApplyAsync(e, Ct));

        var afterCrash = new PaymentRepository(db.DataSource);
        Assert.Equal(ApplyOutcome.Duplicate, await afterCrash.ApplyAsync(e, Ct));

        Assert.Equal(1500, (await db.GetPaymentAsync(e.PaymentId))!.CapturedAmount);
    }

    [Fact]
    public async Task Late_authorized_after_captured_is_recorded_but_does_not_move_the_status_back()
    {
        var paymentId = TestEvents.NewId("pay");

        await _repository.ApplyAsync(TestEvents.Create(paymentId: paymentId, type: PaymentEventTypes.Captured, amount: 1500), Ct);
        var late = await _repository.ApplyAsync(TestEvents.Create(paymentId: paymentId, type: PaymentEventTypes.Authorized, amount: 1500), Ct);

        Assert.Equal(ApplyOutcome.Applied, late);
        Assert.Equal(new PaymentRow(paymentId, "captured", 1500, "EUR"), await db.GetPaymentAsync(paymentId));
        Assert.Equal(2, await db.CountEventsAsync(paymentId));
    }

    public static TheoryData<string[], string> Orders => new()
    {
        { [PaymentEventTypes.Authorized, PaymentEventTypes.Captured], "captured" },
        { [PaymentEventTypes.Captured, PaymentEventTypes.Authorized], "captured" },
        { [PaymentEventTypes.Authorized, PaymentEventTypes.Failed], "failed" },
        { [PaymentEventTypes.Failed, PaymentEventTypes.Authorized], "failed" },
        { [PaymentEventTypes.Failed, PaymentEventTypes.Captured], "captured" },
        { [PaymentEventTypes.Captured, PaymentEventTypes.Failed], "captured" },
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Final_status_does_not_depend_on_arrival_order(string[] types, string expectedStatus)
    {
        var paymentId = TestEvents.NewId("pay");

        foreach (var type in types)
        {
            var amount = type == PaymentEventTypes.Captured ? 1500 : 0;
            await _repository.ApplyAsync(TestEvents.Create(paymentId: paymentId, type: type, amount: amount), Ct);
        }

        Assert.Equal(expectedStatus, (await db.GetPaymentAsync(paymentId))!.Status);
    }

    [Fact]
    public async Task Currency_mismatch_fails_permanently_and_leaves_no_trace()
    {
        var paymentId = TestEvents.NewId("pay");
        await _repository.ApplyAsync(TestEvents.Create(paymentId: paymentId, currency: "EUR", amount: 1500), Ct);

        var mismatch = TestEvents.Create(paymentId: paymentId, currency: "USD", amount: 700);
        await Assert.ThrowsAsync<PermanentEventException>(() => _repository.ApplyAsync(mismatch, Ct));

        // The event row was rolled back together with the payment change.
        Assert.Equal(new PaymentRow(paymentId, "captured", 1500, "EUR"), await db.GetPaymentAsync(paymentId));
        Assert.Equal(1, await db.CountEventsAsync(paymentId));
    }

    [Fact]
    public async Task Occurred_at_with_an_offset_is_stored_as_the_same_instant()
    {
        var occurredAt = new DateTimeOffset(2026, 11, 10, 13, 0, 0, TimeSpan.FromHours(1));
        var e = TestEvents.Create(occurredAt: occurredAt);

        await _repository.ApplyAsync(e, Ct);

        await using var connection = await db.DataSource.OpenConnectionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT occurred_at FROM processed_events WHERE event_id = $1";
        command.Parameters.AddWithValue(e.EventId);
        var stored = (DateTime)(await command.ExecuteScalarAsync(Ct))!;

        Assert.Equal(new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc), stored);
    }

    [Fact]
    public async Task Invalid_body_fails_permanently_before_touching_the_database()
    {
        var processor = new EventProcessor(_repository, NullLogger<EventProcessor>.Instance);
        var paymentId = TestEvents.NewId("pay");
        var body = TestEvents.ToJson(TestEvents.Create(paymentId: paymentId, currency: "eur"));

        await Assert.ThrowsAsync<PermanentEventException>(() => processor.ProcessAsync(body, Ct));

        Assert.Null(await db.GetPaymentAsync(paymentId));
    }

    [Fact]
    public void Running_the_migrations_again_changes_nothing()
    {
        // The worker migrates on every start, so a second run must be a no-op.
        var exception = Record.Exception(() => DatabaseMigrator.Migrate(db.ConnectionString, NullLogger.Instance));

        Assert.Null(exception);
    }
}
