using EPTradeBot.Core.Sandbox;

namespace EPTradeBot.Core.Tests.Trading;

/// <summary>OpenAlgo's sandbox position_manager.get_open_positions rules for the day's book.</summary>
public sealed class SandboxDayPnlTests
{
    private static readonly DateTime Session = new(2026, 11, 3, 21, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ThisSession = Session.AddHours(5);
    private static readonly DateTime Earlier = Session.AddHours(-5);

    private static SandboxPositionState P(string product, int quantity, decimal realized, DateTime updated, decimal average = 100m) =>
        new("X", "NSE", product, quantity, average, realized, updated);

    [Theory]
    [InlineData("MIS", 10, 0, true, true)] // open this session
    [InlineData("MIS", 0, 50, true, true)] // closed this session with a realized P&L
    [InlineData("MIS", 0, 0, true, false)] // closed this session, nothing realized
    [InlineData("NRML", 5, 0, false, true)] // carried NRML
    [InlineData("MIS", 5, 0, false, false)] // an earlier session's MIS was settled at the boundary
    [InlineData("CNC", 5, 0, false, false)]
    [InlineData("NRML", 0, 50, false, false)] // an earlier session's closed position
    public void Lists_what_the_day_book_shows(string product, int quantity, int realized, bool thisSession, bool listed) =>
        Assert.Equal(listed, SandboxDayPnl.Listed(P(product, quantity, realized, thisSession ? ThisSession : Earlier), Session));

    [Fact]
    public void Realized_pnl_from_an_earlier_session_is_not_today_s()
    {
        Assert.Equal(0m, SandboxDayPnl.TodayRealized(P("NRML", 5, 75m, Earlier), Session));
        Assert.Equal(75m, SandboxDayPnl.TodayRealized(P("NRML", 5, 75m, Session), Session));
    }

    [Fact]
    public void Open_positions_are_marked_long_and_short_closed_ones_have_nothing_unrealized()
    {
        Assert.Equal(125m, SandboxDayPnl.Unrealized(P("MIS", 10, 0, ThisSession, 800m), 812.5m));
        Assert.Equal(3750m, SandboxDayPnl.Unrealized(P("NRML", -75, 0, ThisSession, 24100m), 24050m));
        Assert.Equal(0m, SandboxDayPnl.Unrealized(P("MIS", 0, 0, ThisSession, 800m), 900m));
    }

    [Fact]
    public void Contract_value_scales_the_unrealized_pnl()
    {
        // ETHUSD.P: 0.01 ETH a contract.
        Assert.Equal(15m, SandboxDayPnl.Unrealized(P("NRML", 100, 0, ThisSession, 3000m), 3015m, 0.01m));
    }

    [Fact]
    public void Total_is_today_s_realized_plus_unrealized()
    {
        var row = SandboxDayPnl.Row(P("NRML", -75, -120.5m, ThisSession, 24100m), 24050m, 1m, Session);
        Assert.Equal(3629.5m, row.Total);
    }
}
