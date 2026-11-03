using EPTradeBot.Core.Calendar;

namespace EPTradeBot.Core.Tests.Calendar;

/// <summary>Cases of OpenAlgo's get_market_timings_for_date the shipped calendar does not reach.</summary>
public sealed class MarketCalendarTests
{
    private static readonly Dictionary<string, (long, long)> NoStoredTimings = [];
    private static readonly DateOnly Wednesday = new(2026, 11, 4);

    [Fact]
    public void Midnight_is_india_s_whatever_the_server_clock()
    {
        // Recorded from OpenAlgo with the server in India: NSE opened at 1793763900000 on 4 November 2026 (09:15).
        Assert.Equal(1793763900000 - 33_300_000, MarketCalendar.MidnightIst(Wednesday));
    }

    [Fact]
    public void A_settlement_holiday_trades_normal_hours_on_every_exchange()
    {
        var entry = new CalendarDay(Wednesday, "Settlement only", "SETTLEMENT_HOLIDAY", [new("NSE", false, null, null)]);
        var sessions = MarketCalendar.Sessions(Wednesday, entry, NoStoredTimings);
        Assert.Equal(MarketCalendar.Exchanges, sessions.Select(s => s.Exchange));
        Assert.Equal(MarketCalendar.MidnightIst(Wednesday) + 33_300_000, sessions[0].StartTime);
    }

    [Fact]
    public void A_stored_timing_wins_over_the_default_and_the_rest_keep_theirs()
    {
        var sessions = MarketCalendar.Sessions(Wednesday, null, new Dictionary<string, (long, long)> { ["NSE"] = (36_000_000, 50_000_000) });
        var midnight = MarketCalendar.MidnightIst(Wednesday);
        Assert.Equal((midnight + 36_000_000, midnight + 50_000_000), (sessions[0].StartTime!.Value, sessions[0].EndTime!.Value));
        Assert.Equal(midnight + 56_400_000, sessions.Single(s => s.Exchange == "NFO").EndTime);
    }

    [Fact]
    public void A_weekend_without_an_entry_trades_crypto_only()
    {
        var sunday = new DateOnly(2026, 11, 1);
        Assert.Equal(["CRYPTO"], MarketCalendar.Sessions(sunday, null, NoStoredTimings).Select(s => s.Exchange));
    }

    [Fact]
    public void A_weekend_special_session_answers_its_own_windows()
    {
        var sunday = new DateOnly(2026, 11, 1);
        var entry = new CalendarDay(sunday, "Budget", "SPECIAL_SESSION", [new("NSE", true, 1, 2), new("MCX", false, null, null)]);
        Assert.Equal([new ExchangeSession("NSE", 1, 2)], MarketCalendar.Sessions(sunday, entry, NoStoredTimings));
    }

    [Fact]
    public void A_second_row_for_an_exchange_replaces_its_window_in_the_first_one_s_place()
    {
        var entry = new CalendarDay(Wednesday, "Muhurat", "SPECIAL_SESSION",
            [new("NSE", true, 1, 2), new("MCX", true, 3, 4), new("NSE", true, 5, 6)]);
        Assert.Equal([new ExchangeSession("NSE", 5, 6), new ExchangeSession("MCX", 3, 4)], MarketCalendar.Sessions(Wednesday, entry, NoStoredTimings));
    }

    [Fact]
    public void A_trading_holiday_answers_only_the_exchanges_it_opens()
    {
        var entry = new CalendarDay(Wednesday, "Holiday", "TRADING_HOLIDAY", [new("NSE", false, null, null), new("MCX", true, 7, 8)]);
        Assert.Equal([new ExchangeSession("MCX", 7, 8)], MarketCalendar.Sessions(Wednesday, entry, NoStoredTimings));
    }
}
