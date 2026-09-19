namespace NovaWallet.Domain;

public enum TransactionDirection
{
    Credit,
    Debit
}

public class Transaction
{
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public Guid? CounterpartyWalletId { get; private set; }
    public long AmountKobo { get; private set; }
    public TransactionDirection Direction { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Transaction() { }

    public static Transaction Create(Guid walletId, long amountKobo, TransactionDirection direction, Guid? counterpartyWalletId = null)
    {
        if (amountKobo <= 0)
            throw new InvalidAmountException(amountKobo);

        return new Transaction
        {
            Id = UuidV7.NewId(),
            WalletId = walletId,
            CounterpartyWalletId = counterpartyWalletId,
            AmountKobo = amountKobo,
            Direction = direction,
            CreatedAt = DateTime.UtcNow
        };
    }
}
