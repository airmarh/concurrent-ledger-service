namespace NovaWallet.Domain;

// Written in the same DB transaction as the business mutation it describes (see
// TransferService), so the event can never be lost even if the dispatcher crashes
// before publishing it — the next poll picks up any row still missing DispatchedAt.
public class OutboxEvent
{
    public Guid Id { get; private set; }
    public string EventType { get; private set; } = default!;
    public string PayloadJson { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public DateTime? DispatchedAt { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? NextAttemptAt { get; private set; }

    // Set once AttemptCount hits the configured max — stops auto-retrying and
    // needs manual investigation (no dead-letter queue/alerting yet).
    public DateTime? FailedAt { get; private set; }

    private OutboxEvent() { }

    public static OutboxEvent Create(string eventType, string payloadJson, DateTime createdAtUtc)
    {
        return new OutboxEvent
        {
            Id = UuidV7.NewId(),
            EventType = eventType,
            PayloadJson = payloadJson,
            CreatedAt = createdAtUtc
        };
    }
    
    public static OutboxEvent FromPersisted(
        Guid id, string eventType, string payloadJson, DateTime createdAt,
        DateTime? dispatchedAt, int attemptCount, DateTime? nextAttemptAt, DateTime? failedAt)
    {
        return new OutboxEvent
        {
            Id = id,
            EventType = eventType,
            PayloadJson = payloadJson,
            CreatedAt = createdAt,
            DispatchedAt = dispatchedAt,
            AttemptCount = attemptCount,
            NextAttemptAt = nextAttemptAt,
            FailedAt = failedAt
        };
    }

    public void MarkDispatched(DateTime dispatchedAtUtc)
    {
        DispatchedAt = dispatchedAtUtc;
    }

    public void RecordFailure(DateTime nowUtc, int maxAttempts, int baseDelaySeconds, int maxDelaySeconds)
    {
        AttemptCount++;

        if (AttemptCount >= maxAttempts)
        {
            FailedAt = nowUtc;
            return;
        }

        var delaySeconds = Math.Min(baseDelaySeconds * Math.Pow(2, AttemptCount - 1), maxDelaySeconds);
        NextAttemptAt = nowUtc.AddSeconds(delaySeconds);
    }
}

public static class OutboxEventTypes
{
    public const string TransferCompleted = "TransferCompleted";
    public const string ExternalInboundCreditCompleted = "ExternalInboundCreditCompleted";
    public const string ExternalOutboundDebitCompleted = "ExternalOutboundDebitCompleted";
    public const string ExternalOutboundReversed = "ExternalOutboundReversed";
}

public record TransferCompletedPayload(
    Guid DebitTransactionId,
    Guid CreditTransactionId,
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountKobo,
    string CorrelationId);

public record ExternalInboundCreditCompletedPayload(
    Guid TransactionId,
    Guid WalletId,
    long AmountKobo,
    string CorrelationId);

public record ExternalOutboundDebitCompletedPayload(
    Guid TransactionId,
    Guid WalletId,
    long AmountKobo,
    string CorrelationId);

public record ExternalOutboundReversedPayload(
    Guid OriginalTransactionId,
    Guid ReversalTransactionId,
    Guid WalletId,
    long AmountKobo,
    string Reason,
    string CorrelationId);
