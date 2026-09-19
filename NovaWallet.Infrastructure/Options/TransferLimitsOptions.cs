namespace NovaWallet.Infrastructure;

public class TransferLimitsOptions
{
    public const string SectionName = "TransferLimits";

    public long DailyOutboundLimitKobo { get; set; } = 50_000_000;
}


