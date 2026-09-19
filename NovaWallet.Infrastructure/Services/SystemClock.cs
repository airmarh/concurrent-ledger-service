using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
