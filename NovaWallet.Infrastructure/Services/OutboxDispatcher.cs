using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class OutboxDispatcher(NovaWalletDbContext db, IClock clock, OutboxRetryOptions retryOptions, ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    private const int BatchSize = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> DispatchPendingAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var pending = await LockPendingAsync(now, ct);

        var dispatchedCount = 0;

        foreach (var outboxEvent in pending)
        {
            try
            {
                Publish(outboxEvent);
                outboxEvent.MarkDispatched(now);
                dispatchedCount++;
            }
            catch (Exception ex)
            {
                outboxEvent.RecordFailure(now, retryOptions.MaxAttempts, retryOptions.BaseDelaySeconds, retryOptions.MaxDelaySeconds);

                if (outboxEvent.FailedAt is not null)
                {
                    logger.LogError(ex,
                        "Outbox event {OutboxEventId} exhausted {MaxAttempts} attempts and will no longer be retried automatically",
                        outboxEvent.Id, retryOptions.MaxAttempts);
                }
                else
                {
                    logger.LogWarning(ex,
                        "Outbox event {OutboxEventId} failed to dispatch on attempt {AttemptCount}, retrying at {NextAttemptAt}",
                        outboxEvent.Id, outboxEvent.AttemptCount, outboxEvent.NextAttemptAt);
                }
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE outbox_events
                 SET dispatched_at = {outboxEvent.DispatchedAt},
                     attempt_count = {outboxEvent.AttemptCount},
                     next_attempt_at = {outboxEvent.NextAttemptAt},
                     failed_at = {outboxEvent.FailedAt}
                 WHERE id = {outboxEvent.Id}
                 """, ct);
        }

        await transaction.CommitAsync(ct);

        return dispatchedCount;
    }

    private async Task<List<OutboxEvent>> LockPendingAsync(DateTime now, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT id, event_type, payload_json, created_at, dispatched_at, attempt_count, next_attempt_at, failed_at
             FROM outbox_events
             WHERE dispatched_at IS NULL
               AND failed_at IS NULL
               AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
             ORDER BY created_at
             LIMIT {BatchSize}
             FOR UPDATE SKIP LOCKED
             """;
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var param = command.CreateParameter();
        param.ParameterName = "now";
        param.Value = now;
        command.Parameters.Add(param);

        var results = new List<OutboxEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(OutboxEvent.FromPersisted(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
        }

        return results;
    }

    // "Publishing" is a structured log line, not a real message broker — the outbox's
    // durability guarantee is what's being demonstrated.
    private void Publish(OutboxEvent outboxEvent)
    {
        switch (outboxEvent.EventType)
        {
            case OutboxEventTypes.TransferCompleted:
                PublishTransferCompleted(outboxEvent);
                break;
            case OutboxEventTypes.ExternalInboundCreditCompleted:
                PublishExternalInboundCreditCompleted(outboxEvent);
                break;
            case OutboxEventTypes.ExternalOutboundDebitCompleted:
                PublishExternalOutboundDebitCompleted(outboxEvent);
                break;
            case OutboxEventTypes.ExternalOutboundReversed:
                PublishExternalOutboundReversed(outboxEvent);
                break;
            default:
                logger.LogWarning("Skipping outbox event {OutboxEventId} of unknown type {EventType}", outboxEvent.Id, outboxEvent.EventType);
                break;
        }
    }

    private void PublishTransferCompleted(OutboxEvent outboxEvent)
    {
        var payload = JsonSerializer.Deserialize<TransferCompletedPayload>(outboxEvent.PayloadJson, JsonOptions)!;

        logger.LogInformation(
            "TransferCompleted {CorrelationId}: debit {DebitTransactionId} / credit {CreditTransactionId}, {FromWalletId} -> {ToWalletId}, {AmountKobo} kobo",
            payload.CorrelationId, payload.DebitTransactionId, payload.CreditTransactionId,
            payload.FromWalletId, payload.ToWalletId, payload.AmountKobo);
    }

    private void PublishExternalInboundCreditCompleted(OutboxEvent outboxEvent)
    {
        var payload = JsonSerializer.Deserialize<ExternalInboundCreditCompletedPayload>(outboxEvent.PayloadJson, JsonOptions)!;

        logger.LogInformation(
            "ExternalInboundCreditCompleted {CorrelationId}: transaction {TransactionId}, wallet {WalletId}, {AmountKobo} kobo",
            payload.CorrelationId, payload.TransactionId, payload.WalletId, payload.AmountKobo);
    }

    private void PublishExternalOutboundDebitCompleted(OutboxEvent outboxEvent)
    {
        var payload = JsonSerializer.Deserialize<ExternalOutboundDebitCompletedPayload>(outboxEvent.PayloadJson, JsonOptions)!;

        logger.LogInformation(
            "ExternalOutboundDebitCompleted {CorrelationId}: transaction {TransactionId}, wallet {WalletId}, {AmountKobo} kobo",
            payload.CorrelationId, payload.TransactionId, payload.WalletId, payload.AmountKobo);
    }

    private void PublishExternalOutboundReversed(OutboxEvent outboxEvent)
    {
        var payload = JsonSerializer.Deserialize<ExternalOutboundReversedPayload>(outboxEvent.PayloadJson, JsonOptions)!;

        logger.LogInformation(
            "ExternalOutboundReversed {CorrelationId}: original {OriginalTransactionId} / reversal {ReversalTransactionId}, wallet {WalletId}, {AmountKobo} kobo, reason {Reason}",
            payload.CorrelationId, payload.OriginalTransactionId, payload.ReversalTransactionId,
            payload.WalletId, payload.AmountKobo, payload.Reason);
    }
}
