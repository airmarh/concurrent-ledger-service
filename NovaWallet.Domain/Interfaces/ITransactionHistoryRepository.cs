namespace NovaWallet.Domain;

public interface ITransactionHistoryRepository
{
    Task<(IReadOnlyList<Transaction> Items, int TotalCount)> GetPageAsync(
        Guid walletId, TransactionHistoryQuery query, CancellationToken ct = default);
}
