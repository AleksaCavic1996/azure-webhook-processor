using Dapper;
using Npgsql;
using WebhookProcessor.Core;

namespace WebhookProcessor.Worker;

public enum ApplyOutcome
{
    Applied,
    Duplicate,
}

public sealed class PaymentRepository(NpgsqlDataSource dataSource)
{
    // A concurrent delivery of the same event waits here until the first transaction ends.
    // If that one committed, this insert affects 0 rows. If it rolled back, this one takes over.
    private const string InsertEventSql = """
        INSERT INTO processed_events (event_id, payment_id, event_type, amount, currency, occurred_at)
        VALUES (@EventId, @PaymentId, @Type, @Amount, @Currency, @OccurredAt)
        ON CONFLICT (event_id) DO NOTHING
        """;

    // The queue does not guarantee order, so status only moves forward along a fixed order.
    // That makes the final state the same whichever order the events arrive in.
    private const string UpsertPaymentSql = """
        INSERT INTO payments (payment_id, status, captured_amount, currency)
        VALUES (@PaymentId, @Status, @CapturedAmount, @Currency)
        ON CONFLICT (payment_id) DO UPDATE SET
            status = CASE
                WHEN array_position(ARRAY['authorized', 'failed', 'captured'], EXCLUDED.status)
                   > array_position(ARRAY['authorized', 'failed', 'captured'], payments.status)
                THEN EXCLUDED.status
                ELSE payments.status
            END,
            captured_amount = payments.captured_amount + EXCLUDED.captured_amount,
            updated_at = now()
        WHERE payments.currency = EXCLUDED.currency
        """;

    /// <summary>Records the event and changes the payment in one transaction, so both happen or neither does.</summary>
    public async Task<ApplyOutcome> ApplyAsync(PaymentEvent e, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            InsertEventSql,
            new { e.EventId, e.PaymentId, e.Type, e.Amount, e.Currency, OccurredAt = e.OccurredAt.ToUniversalTime() },
            transaction,
            cancellationToken: cancellationToken));

        if (inserted == 0)
        {
            return ApplyOutcome.Duplicate;
        }

        var changedPayments = await connection.ExecuteAsync(new CommandDefinition(
            UpsertPaymentSql,
            new
            {
                e.PaymentId,
                Status = ToStatus(e.Type),
                CapturedAmount = e.Type == PaymentEventTypes.Captured ? e.Amount : 0,
                e.Currency,
            },
            transaction,
            cancellationToken: cancellationToken));

        if (changedPayments == 0)
        {
            throw new PermanentEventException(
                $"Event currency {e.Currency} differs from the currency of payment {e.PaymentId}.");
        }

        await transaction.CommitAsync(cancellationToken);
        return ApplyOutcome.Applied;
    }

    private static string ToStatus(string eventType) => eventType switch
    {
        PaymentEventTypes.Authorized => "authorized",
        PaymentEventTypes.Captured => "captured",
        PaymentEventTypes.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unknown event type."),
    };
}
