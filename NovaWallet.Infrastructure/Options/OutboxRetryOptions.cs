namespace NovaWallet.Infrastructure;

public class OutboxRetryOptions
{
    public const string SectionName = "OutboxRetry";

    public int MaxAttempts { get; set; } = 10;
    public int BaseDelaySeconds { get; set; } = 5;
    public int MaxDelaySeconds { get; set; } = 300;
}
