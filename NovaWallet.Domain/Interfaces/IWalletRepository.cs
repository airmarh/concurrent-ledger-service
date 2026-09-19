namespace NovaWallet.Domain;

public interface IWalletRepository
{
    Task AddAsync(Wallet wallet, CancellationToken ct = default);
    Task<Wallet?> GetByIdAsync(Guid walletId, CancellationToken ct = default);
    Task<Wallet?> GetByAccountNumberAsync(string accountNumber, CancellationToken ct = default);
    Task<IReadOnlyList<Wallet>> GetByCustomerIdAsync(string customerId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string customerId, string currency, string accountType, CancellationToken ct = default);
}
