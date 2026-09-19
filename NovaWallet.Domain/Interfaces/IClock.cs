namespace NovaWallet.Domain;

public interface IClock
{
    DateTime UtcNow { get; }
}
