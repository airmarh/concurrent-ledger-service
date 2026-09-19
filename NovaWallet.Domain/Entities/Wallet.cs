namespace NovaWallet.Domain;

public class Wallet
{
    public Guid WalletId { get; private set; }
    public string AccountNumber { get; private set; } = default!;
    public string CustomerId { get; private set; } = default!;
    public string Currency { get; private set; } = "NGN";
    public string AccountType { get; private set; } = "SAVINGS";
    public long BalanceKobo { get; private set; }

    // True only for the fixed settlement account: it represents money owed to the
    // outside world, so unlike every customer wallet, its balance is expected to go negative.
    public bool AllowsNegativeBalance { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Wallet() { }

    public static Wallet Create(string customerId, string currency = "NGN", string accountType = "SAVINGS")
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("customerId is required.", nameof(customerId));

        return new Wallet
        {
            WalletId = UuidV7.NewId(),
            AccountNumber = AccountNumberGenerator.Generate(),
            CustomerId = customerId,
            Currency = currency,
            AccountType = accountType,
            BalanceKobo = 0,
            AllowsNegativeBalance = false,
            CreatedAt = DateTime.UtcNow
        };
    }

    // Not customer-facing and never created through POST /wallets: seeded once by
    // migration at a fixed wallet_id/account_number so callers can reference it directly,
    // no lookup needed. Both the inbound and outbound settlement accounts share the same
    // reserved customer_id — the (customer_id, currency, account_type) uniqueness rule
    // already lets one customer hold more than one wallet, distinguished by account_type.
    public static Wallet CreateSettlementAccount(Guid walletId, string accountNumber, string accountType, string currency)
    {
        return new Wallet
        {
            WalletId = walletId,
            AccountNumber = accountNumber,
            CustomerId = WellKnownWalletIds.SystemSettlementCustomerId,
            Currency = currency,
            AccountType = accountType,
            BalanceKobo = 0,
            AllowsNegativeBalance = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static Wallet FromPersisted(Guid walletId, string accountNumber, string customerId, string currency, string accountType, long balanceKobo, bool allowsNegativeBalance, DateTime createdAt)
    {
        return new Wallet
        {
            WalletId = walletId,
            AccountNumber = accountNumber,
            CustomerId = customerId,
            Currency = currency,
            AccountType = accountType,
            BalanceKobo = balanceKobo,
            AllowsNegativeBalance = allowsNegativeBalance,
            CreatedAt = createdAt
        };
    }

    // For the rare event a freshly generated account number collides with an
    // existing one — lets the insert retry with a new number instead of failing.
    public void RegenerateAccountNumber() => AccountNumber = AccountNumberGenerator.Generate();

    public void Debit(long amountKobo)
    {
        if (amountKobo <= 0)
            throw new InvalidAmountException(amountKobo);
        if (!AllowsNegativeBalance && BalanceKobo < amountKobo)
            throw new InsufficientFundsException(WalletId, amountKobo, BalanceKobo);

        BalanceKobo -= amountKobo;
    }

    public void Credit(long amountKobo)
    {
        if (amountKobo <= 0)
            throw new InvalidAmountException(amountKobo);

        BalanceKobo += amountKobo;
    }
}
