namespace NovaWallet.Domain;

public interface ITransferService
{
    Task<TransferResult> TransferAsync(Guid fromWalletId, Guid toWalletId, long amountKobo, string correlationId, CancellationToken ct = default);
}

public record TransferResult(
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountKobo,
    long FromBalanceKobo,
    long ToBalanceKobo,
    Guid DebitTransactionId,
    Guid CreditTransactionId);
