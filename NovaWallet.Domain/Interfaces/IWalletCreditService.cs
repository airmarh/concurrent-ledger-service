namespace NovaWallet.Domain;

public interface IWalletCreditService
{
    Task<CreditResult> CreditAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct = default);
}

public record CreditResult(Guid WalletId, Guid TransactionId, long BalanceKobo);
