namespace NovaWallet.Domain;

public interface IOutboundService
{
    Task<OutboundDebitResult> DebitAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct = default);

    Task<ReversalResult> ReverseAsync(Guid originalTransactionId, string reason, string actor, string correlationId, CancellationToken ct = default);
}

public record OutboundDebitResult(Guid WalletId, Guid TransactionId, long BalanceKobo);

public record ReversalResult(Guid WalletId, Guid OriginalTransactionId, Guid ReversalTransactionId, long BalanceKobo);
