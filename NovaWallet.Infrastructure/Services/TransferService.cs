using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;
using Transaction = NovaWallet.Domain.Transaction;

namespace NovaWallet.Infrastructure;

public class TransferService(NovaWalletDbContext db, IClock clock, TransferLimitsOptions limits) : ITransferService
{
    private static readonly JsonSerializerOptions OutboxPayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TransferResult> TransferAsync(Guid fromWalletId, Guid toWalletId, long amountKobo, string correlationId, CancellationToken ct = default)
    {
        if (amountKobo <= 0)
            throw new InvalidAmountException(amountKobo);
        if (fromWalletId == toWalletId)
            throw new SameWalletTransferException(fromWalletId);

        if (fromWalletId == WellKnownWalletIds.NgnInboundSettlementAccount || fromWalletId == WellKnownWalletIds.NgnOutboundSettlementAccount)
            throw new ReservedWalletException(fromWalletId);
        if (toWalletId == WellKnownWalletIds.NgnInboundSettlementAccount || toWalletId == WellKnownWalletIds.NgnOutboundSettlementAccount)
            throw new ReservedWalletException(toWalletId);

        // Always lock the smaller wallet_id first, regardless of transfer direction,
        // so two transfers moving money in opposite directions between the same pair
        // of wallets can never deadlock waiting on each other's row locks.
        var (firstId, secondId) = fromWalletId.CompareTo(toWalletId) < 0
            ? (fromWalletId, toWalletId)
            : (toWalletId, fromWalletId);

        await using var dbTransaction = await db.Database.BeginTransactionAsync(ct);

        var first = await WalletLock.LockAsync(db, firstId, ct) ?? throw new WalletNotFoundException(firstId);
        var second = await WalletLock.LockAsync(db, secondId, ct) ?? throw new WalletNotFoundException(secondId);

        var fromWallet = fromWalletId == firstId ? first : second;
        var toWallet = toWalletId == firstId ? first : second;

        var fromBalanceBefore = fromWallet.BalanceKobo;
        var toBalanceBefore = toWallet.BalanceKobo;
        
        var dayStartUtc = WestAfricaTime.GetCurrentDayStartUtc(clock.UtcNow);
        var spentTodayKobo = await db.Transactions
            .Where(t => t.WalletId == fromWalletId
                        && t.Direction == TransactionDirection.Debit
                        && t.CreatedAt >= dayStartUtc)
            .SumAsync(t => t.AmountKobo, ct);

        if (spentTodayKobo + amountKobo > limits.DailyOutboundLimitKobo)
            throw new DailyLimitExceededException(fromWalletId, limits.DailyOutboundLimitKobo, spentTodayKobo, amountKobo);

        // Domain-level checks (insufficient funds, invalid amount) run while we hold
        // both row locks, so no concurrent transfer can change these balances out
        // from under us between the check and the write.
        fromWallet.Debit(amountKobo);
        toWallet.Credit(amountKobo);

        var debitTxn = Transaction.Create(fromWalletId, amountKobo, TransactionDirection.Debit, toWalletId);
        var creditTxn = Transaction.Create(toWalletId, amountKobo, TransactionDirection.Credit, fromWalletId);
        var debitAudit = AuditLogEntry.Create(fromWalletId, fromBalanceBefore, fromWallet.BalanceKobo, "INTERNAL_TRANSFER", correlationId);
        var creditAudit = AuditLogEntry.Create(toWalletId, toBalanceBefore, toWallet.BalanceKobo, "INTERNAL_TRANSFER", correlationId);

        db.Transactions.AddRange(debitTxn, creditTxn);
        db.AuditLog.AddRange(debitAudit, creditAudit);
        
        var payload = JsonSerializer.Serialize(
            new TransferCompletedPayload(debitTxn.Id, creditTxn.Id, fromWalletId, toWalletId, amountKobo, correlationId),
            OutboxPayloadJsonOptions);
        db.Outbox.Add(OutboxEvent.Create(OutboxEventTypes.TransferCompleted, payload, clock.UtcNow));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {fromWallet.BalanceKobo} WHERE wallet_id = {fromWalletId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {toWallet.BalanceKobo} WHERE wallet_id = {toWalletId}", ct);

        await db.SaveChangesAsync(ct);
        await dbTransaction.CommitAsync(ct);

        return new TransferResult(
            fromWalletId, toWalletId, amountKobo,
            fromWallet.BalanceKobo, toWallet.BalanceKobo,
            debitTxn.Id, creditTxn.Id);
    }
}
