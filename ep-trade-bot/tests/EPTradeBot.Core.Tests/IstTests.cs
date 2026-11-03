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

    // Expected values from OpenAlgo's sandbox/session_boundary.last_session_expiry_utc, run on the same IST instants.
    [Theory]
    [InlineData(2026, 11, 4, 2, 59, "2026-11-02T21:30:00")]
    [InlineData(2026, 11, 4, 3, 0, "2026-11-03T21:30:00")]
    [InlineData(2026, 11, 4, 15, 0, "2026-11-03T21:30:00")]
    [InlineData(2026, 1, 1, 0, 30, "2025-12-30T21:30:00")]
    public void Session_boundary_is_the_last_0300_ist(int y, int mo, int d, int h, int mi, string expected)
    {
        var utc = new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc) - Ist.Offset;
        var boundary = Ist.LastSessionExpiryUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), boundary);
        Assert.Equal(DateTimeKind.Utc, boundary.Kind);
    }

    [Fact]
    public void Session_boundary_takes_another_expiry_time()
    {
        var utc = DateTime.SpecifyKind(new DateTime(2026, 11, 4, 9, 0, 0) - Ist.Offset, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 3, 3, 45, 0), Ist.LastSessionExpiryUtc(utc, new TimeOnly(9, 15)));
    }
}
