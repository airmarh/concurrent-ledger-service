using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class TransactionHistoryRepository(NovaWalletDbContext db) : ITransactionHistoryRepository
{
    public async Task<(IReadOnlyList<Transaction> Items, int TotalCount)> GetPageAsync(
        Guid walletId, TransactionHistoryQuery query, CancellationToken ct = default)
    {
        var baseQuery = db.Transactions.Where(t => t.WalletId == walletId);

        var totalCount = await baseQuery.CountAsync(ct);
        var items = await baseQuery
            .OrderByDescending(t => t.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
