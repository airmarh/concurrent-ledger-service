namespace NovaWallet.Api;

public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int PermitLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 60;
}
