using NovaWallet.Domain;

namespace NovaWallet.IntegrationTests;

internal class FixedClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; } = utcNow;
}
