namespace NovaWallet.Domain;

public class AuditLogEntry
{
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public long BalanceBeforeKobo { get; private set; }
    public long BalanceAfterKobo { get; private set; }
    public string Actor { get; private set; } = null!;
    public string CorrelationId { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }

    private AuditLogEntry() { }

    public static AuditLogEntry Create(Guid walletId, long balanceBeforeKobo, long balanceAfterKobo, string actor, string correlationId)
    {
        return new AuditLogEntry
        {
            Id = UuidV7.NewId(),
            WalletId = walletId,
            BalanceBeforeKobo = balanceBeforeKobo,
            BalanceAfterKobo = balanceAfterKobo,
            Actor = actor,
            CorrelationId = correlationId,
            CreatedAt = DateTime.UtcNow
        };
    }
}
