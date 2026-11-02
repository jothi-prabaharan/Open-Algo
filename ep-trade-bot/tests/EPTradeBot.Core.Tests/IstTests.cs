using EPTradeBot.Core.Time;

namespace EPTradeBot.Core.Tests;

public sealed class IstTests
{
    [Fact]
    public void Utc_converts_to_ist_wall_clock_at_plus_five_thirty()
    {
        var utc = new DateTime(2026, 11, 2, 3, 45, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 15, 0), Ist.FromUtc(utc));
    }

    [Fact]
    public void Trading_day_rolls_over_at_ist_midnight_not_utc_midnight()
    {
        var utc = new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 11, 3), Ist.TradingDay(utc));
    }
}
