using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class OutboundService(NovaWalletDbContext db, IClock clock, TransferLimitsOptions limits) : IOutboundService
{
    private static readonly JsonSerializerOptions OutboxPayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OutboundDebitResult> DebitAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct = default)
    {
        if (amountKobo <= 0)
            throw new InvalidAmountException(amountKobo);

        var settlementAccountId = WellKnownWalletIds.NgnOutboundSettlementAccount;
        if (walletId == settlementAccountId)
            throw new ReservedWalletException(walletId);
        
        var (firstId, secondId) = walletId.CompareTo(settlementAccountId) < 0
            ? (walletId, settlementAccountId)
            : (settlementAccountId, walletId);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var first = await WalletLock.LockAsync(db, firstId, ct) ?? throw new WalletNotFoundException(firstId);
        var second = await WalletLock.LockAsync(db, secondId, ct) ?? throw new WalletNotFoundException(secondId);

        var targetWallet = walletId == firstId ? first : second;
        var settlementWallet = walletId == firstId ? second : first;

        var targetBalanceBefore = targetWallet.BalanceKobo;
        var settlementBalanceBefore = settlementWallet.BalanceKobo;
        
        // Running this while still holding targetWallet's row lock is what makes it
        // concurrency-safe, same reasoning as TransferService.
        var dayStartUtc = WestAfricaTime.GetCurrentDayStartUtc(clock.UtcNow);
        var spentTodayKobo = await db.Transactions
            .Where(t => t.WalletId == walletId
                        && t.Direction == TransactionDirection.Debit
                        && t.CreatedAt >= dayStartUtc)
            .SumAsync(t => t.AmountKobo, ct);

        if (spentTodayKobo + amountKobo > limits.DailyOutboundLimitKobo)
            throw new DailyLimitExceededException(walletId, limits.DailyOutboundLimitKobo, spentTodayKobo, amountKobo);

        // The matching double-entry leg: money debited from the customer's wallet is
        // credited to the outbound settlement account, which absorbs it as a liability
        // owed to the outside (simulated NIP network) until either it's confirmed
        // received or ReverseAsync below undoes it.
        targetWallet.Debit(amountKobo);
        settlementWallet.Credit(amountKobo);

        var debitTxn = Transaction.Create(walletId, amountKobo, TransactionDirection.Debit, settlementAccountId);
        var settlementCreditTxn = Transaction.Create(settlementAccountId, amountKobo, TransactionDirection.Credit, walletId);
        var debitAudit = AuditLogEntry.Create(walletId, targetBalanceBefore, targetWallet.BalanceKobo, actor, correlationId);
        var settlementAudit = AuditLogEntry.Create(settlementAccountId, settlementBalanceBefore, settlementWallet.BalanceKobo, actor, correlationId);

        db.Transactions.AddRange(debitTxn, settlementCreditTxn);
        db.AuditLog.AddRange(debitAudit, settlementAudit);

        var debitPayload = JsonSerializer.Serialize(
            new ExternalOutboundDebitCompletedPayload(debitTxn.Id, walletId, amountKobo, correlationId),
            OutboxPayloadJsonOptions);
        db.Outbox.Add(OutboxEvent.Create(OutboxEventTypes.ExternalOutboundDebitCompleted, debitPayload, clock.UtcNow));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {targetWallet.BalanceKobo} WHERE wallet_id = {walletId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {settlementWallet.BalanceKobo} WHERE wallet_id = {settlementAccountId}", ct);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new OutboundDebitResult(walletId, debitTxn.Id, targetWallet.BalanceKobo);
    }

    public async Task<ReversalResult> ReverseAsync(Guid originalTransactionId, string reason, string actor, string correlationId, CancellationToken ct = default)
    {
        var settlementAccountId = WellKnownWalletIds.NgnOutboundSettlementAccount;
        
        var original = await db.Transactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == originalTransactionId, ct)
            ?? throw new TransactionNotFoundException(originalTransactionId);

        if (original.Direction != TransactionDirection.Debit || original.CounterpartyWalletId != settlementAccountId)
            throw new TransactionNotReversibleException(originalTransactionId);

        var walletId = original.WalletId;
        var amountKobo = original.AmountKobo;

        var (firstId, secondId) = walletId.CompareTo(settlementAccountId) < 0
            ? (walletId, settlementAccountId)
            : (settlementAccountId, walletId);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var first = await WalletLock.LockAsync(db, firstId, ct) ?? throw new WalletNotFoundException(firstId);
        var second = await WalletLock.LockAsync(db, secondId, ct) ?? throw new WalletNotFoundException(secondId);

        var targetWallet = walletId == firstId ? first : second;
        var settlementWallet = walletId == firstId ? second : first;

        var targetBalanceBefore = targetWallet.BalanceKobo;
        var settlementBalanceBefore = settlementWallet.BalanceKobo;

        targetWallet.Credit(amountKobo);
        settlementWallet.Debit(amountKobo);

        var reversalCreditTxn = Transaction.Create(walletId, amountKobo, TransactionDirection.Credit, settlementAccountId);
        var settlementDebitTxn = Transaction.Create(settlementAccountId, amountKobo, TransactionDirection.Debit, walletId);
        var creditAudit = AuditLogEntry.Create(walletId, targetBalanceBefore, targetWallet.BalanceKobo, actor, correlationId);
        var settlementAudit = AuditLogEntry.Create(settlementAccountId, settlementBalanceBefore, settlementWallet.BalanceKobo, actor, correlationId);

        db.Transactions.AddRange(reversalCreditTxn, settlementDebitTxn);
        db.AuditLog.AddRange(creditAudit, settlementAudit);
        db.TransactionReversals.Add(TransactionReversal.Create(originalTransactionId, reversalCreditTxn.Id, reason));

        var reversalPayload = JsonSerializer.Serialize(
            new ExternalOutboundReversedPayload(originalTransactionId, reversalCreditTxn.Id, walletId, amountKobo, reason, correlationId),
            OutboxPayloadJsonOptions);
        db.Outbox.Add(OutboxEvent.Create(OutboxEventTypes.ExternalOutboundReversed, reversalPayload, clock.UtcNow));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {targetWallet.BalanceKobo} WHERE wallet_id = {walletId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {settlementWallet.BalanceKobo} WHERE wallet_id = {settlementAccountId}", ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
        {
            throw new TransactionAlreadyReversedException(originalTransactionId);
        }

        await transaction.CommitAsync(ct);

        return new ReversalResult(walletId, originalTransactionId, reversalCreditTxn.Id, targetWallet.BalanceKobo);
    }

    private static bool IsDuplicateReversal(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_transaction_reversals" };
}
