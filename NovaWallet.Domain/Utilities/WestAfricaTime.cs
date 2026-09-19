namespace NovaWallet.Domain;

public static class WestAfricaTime
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(1);

    public static DateTime GetCurrentDayStartUtc(DateTime nowUtc)
    {
        var watNow = nowUtc.Add(Offset);
        var watDayStart = watNow.Date;
        return DateTime.SpecifyKind(watDayStart.Subtract(Offset), DateTimeKind.Utc);
    }
}
