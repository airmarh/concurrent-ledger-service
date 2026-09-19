using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class WalletCreditService(NovaWalletDbContext db, IClock clock) : IWalletCreditService
{
    private static readonly JsonSerializerOptions OutboxPayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CreditResult> CreditAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct = default)
    {
        if (amountKobo <= 0)
            throw new InvalidAmountException(amountKobo);

        var settlementAccountId = WellKnownWalletIds.NgnInboundSettlementAccount;
        if (walletId == settlementAccountId)
            throw new WalletNotFoundException(walletId);

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

        // The matching double-entry leg: money credited to the customer's wallet is
        // debited from the inbound settlement account, which is allowed to go negative
        // since it represents a liability to the (simulated external NIP network), not
        // an actual pool of funds. Kept separate from the outbound settlement account so
        // inbound and outbound exposure can be reconciled independently.
        targetWallet.Credit(amountKobo);
        settlementWallet.Debit(amountKobo);

        var creditTxn = Transaction.Create(walletId, amountKobo, TransactionDirection.Credit, settlementAccountId);
        var settlementDebitTxn = Transaction.Create(settlementAccountId, amountKobo, TransactionDirection.Debit, walletId);
        var creditAudit = AuditLogEntry.Create(walletId, targetBalanceBefore, targetWallet.BalanceKobo, actor, correlationId);
        var settlementAudit = AuditLogEntry.Create(settlementAccountId, settlementBalanceBefore, settlementWallet.BalanceKobo, actor, correlationId);

        db.Transactions.AddRange(creditTxn, settlementDebitTxn);
        db.AuditLog.AddRange(creditAudit, settlementAudit);

        var payload = JsonSerializer.Serialize(
            new ExternalInboundCreditCompletedPayload(creditTxn.Id, walletId, amountKobo, correlationId),
            OutboxPayloadJsonOptions);
        db.Outbox.Add(OutboxEvent.Create(OutboxEventTypes.ExternalInboundCreditCompleted, payload, clock.UtcNow));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {targetWallet.BalanceKobo} WHERE wallet_id = {walletId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_kobo = {settlementWallet.BalanceKobo} WHERE wallet_id = {settlementAccountId}", ct);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new CreditResult(walletId, creditTxn.Id, targetWallet.BalanceKobo);
    }
}
