namespace NovaWallet.Domain;

// original_transaction_id is the primary key, not a separate unique index, so it
// doubles as the uniqueness guarantee: a concurrent second reversal hits a PK
// violation instead of silently reversing the same debit twice.
public class TransactionReversal
{
    public Guid OriginalTransactionId { get; private set; }
    public Guid ReversalTransactionId { get; private set; }
    public string Reason { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }

    private TransactionReversal() { }

    public static TransactionReversal Create(Guid originalTransactionId, Guid reversalTransactionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required.", nameof(reason));

        return new TransactionReversal
        {
            OriginalTransactionId = originalTransactionId,
            ReversalTransactionId = reversalTransactionId,
            Reason = reason,
            CreatedAt = DateTime.UtcNow
        };
    }
}
