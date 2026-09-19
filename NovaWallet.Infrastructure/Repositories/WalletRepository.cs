using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class WalletRepository(NovaWalletDbContext db) : IWalletRepository
{
    private const int MaxAccountNumberCollisionAttempts = 5;

    public async Task AddAsync(Wallet wallet, CancellationToken ct = default)
    {
        db.Wallets.Add(wallet);

        for (var attempt = 1; attempt <= MaxAccountNumberCollisionAttempts; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxAccountNumberCollisionAttempts && IsAccountNumberCollision(ex))
            {
                db.Entry(wallet).State = EntityState.Detached;
                wallet.RegenerateAccountNumber();
                db.Wallets.Add(wallet);
            }
        }
    }

    public Task<Wallet?> GetByIdAsync(Guid walletId, CancellationToken ct = default)
    {
        return db.Wallets.SingleOrDefaultAsync(w => w.WalletId == walletId, ct);
    }

    public Task<Wallet?> GetByAccountNumberAsync(string accountNumber, CancellationToken ct = default)
    {
        return db.Wallets.SingleOrDefaultAsync(w => w.AccountNumber == accountNumber, ct);
    }

    public async Task<IReadOnlyList<Wallet>> GetByCustomerIdAsync(string customerId, CancellationToken ct = default)
    {
        return await db.Wallets
            .Where(w => w.CustomerId == customerId)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(ct);
    }

    public Task<bool> ExistsAsync(string customerId, string currency, string accountType, CancellationToken ct = default)
    {
        return db.Wallets.AnyAsync(
            w => w.CustomerId == customerId && w.Currency == currency && w.AccountType == accountType, ct);
    }

    private static bool IsAccountNumberCollision(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_wallets_account_number" };
}
