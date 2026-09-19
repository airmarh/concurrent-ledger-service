using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class WestAfricaTimeTests
{
    [Fact]
    public void GetCurrentDayStartUtc_JustBeforeWatMidnight_ReturnsPreviousDayBoundary()
    {
        // 2026-09-18T22:59:59Z is 2026-09-18T23:59:59 WAT — still the 18th in WAT.
        var nowUtc = new DateTime(2026, 9, 18, 22, 59, 59, DateTimeKind.Utc);

        var dayStartUtc = WestAfricaTime.GetCurrentDayStartUtc(nowUtc);

        Assert.Equal(new DateTime(2026, 9, 17, 23, 0, 0, DateTimeKind.Utc), dayStartUtc);
    }

    [Fact]
    public void GetCurrentDayStartUtc_ExactlyAtWatMidnight_RollsOverToNewDay()
    {
        // 2026-09-18T23:00:00Z is exactly 2026-09-19T00:00:00 WAT.
        var nowUtc = new DateTime(2026, 9, 18, 23, 0, 0, DateTimeKind.Utc);

        var dayStartUtc = WestAfricaTime.GetCurrentDayStartUtc(nowUtc);

        Assert.Equal(new DateTime(2026, 9, 18, 23, 0, 0, DateTimeKind.Utc), dayStartUtc);
    }

    [Fact]
    public void GetCurrentDayStartUtc_JustAfterWatMidnight_UsesNewDayBoundary()
    {
        var nowUtc = new DateTime(2026, 9, 18, 23, 0, 1, DateTimeKind.Utc);

        var dayStartUtc = WestAfricaTime.GetCurrentDayStartUtc(nowUtc);

        Assert.Equal(new DateTime(2026, 9, 18, 23, 0, 0, DateTimeKind.Utc), dayStartUtc);
    }

    [Fact]
    public void GetCurrentDayStartUtc_MiddayUtc_ReturnsSameCalendarDayBoundaryOneHourEarlier()
    {
        var nowUtc = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

        var dayStartUtc = WestAfricaTime.GetCurrentDayStartUtc(nowUtc);

        Assert.Equal(new DateTime(2026, 9, 17, 23, 0, 0, DateTimeKind.Utc), dayStartUtc);
    }
}
