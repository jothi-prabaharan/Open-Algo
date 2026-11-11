using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Scheduling;

namespace EPTradeBot.Core.Tests;

public sealed class ScheduleRulesTests
{
    // November 2026: the 11th is a Wednesday, the 14th a Saturday, the 15th a Sunday, the 16th a Monday.
    private static readonly DateOnly Wed = new(2026, 11, 11);
    private static readonly DateOnly Sat = new(2026, 11, 14);
    private static readonly DateOnly Sun = new(2026, 11, 15);
    private static readonly DateOnly Mon = new(2026, 11, 16);

    private static IReadOnlyList<ExchangeSession> Normal(DateOnly d) => MarketCalendar.Sessions(d, null, MarketCalendar.DefaultOffsets);

    private static IReadOnlyList<ExchangeSession> Holiday(DateOnly d) =>
        MarketCalendar.Sessions(d, new CalendarDay(d, "Closed", "TRADING_HOLIDAY", []), MarketCalendar.DefaultOffsets);

    /// <summary>An IST wall-clock moment as the UTC instant the scheduler sees.</summary>
    private static DateTime At(DateOnly d, int h, int m, int s = 0) =>
        DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(h, m, s)) - TimeSpan.FromHours(5.5), DateTimeKind.Utc);

    private static StrategySchedule Make(string exchange = "NSE", string? start = "09:30", string? stop = "15:00", TradingDays days = TradingDays.Weekdays,
        bool scheduled = true, bool running = false, bool manual = false, string? paused = null) =>
        new(exchange, start is null ? null : TimeOnly.Parse(start), stop is null ? null : TimeOnly.Parse(stop), days, scheduled, true, running, manual, paused);

    [Fact]
    public void The_cron_is_in_quartz_form_and_lists_only_the_chosen_days()
    {
        Assert.Equal("0 15 9 ? * MON,TUE,WED,THU,FRI", ScheduleRules.Cron(new TimeOnly(9, 15), TradingDays.Weekdays));
        Assert.Equal("0 0 17 ? * MON,SAT", ScheduleRules.Cron(new TimeOnly(17, 0), TradingDays.Monday | TradingDays.Saturday));
        Assert.Equal("0 59 23 ? * *", ScheduleRules.Cron(new TimeOnly(23, 59), TradingDays.Weekdays | TradingDays.Saturday | TradingDays.Sunday));
        Assert.Equal("0 5 6 ? * *", ScheduleRules.Cron(new TimeOnly(6, 5), TradingDays.None));
    }

    [Theory]
    [InlineData(11, TradingDays.Wednesday)]
    [InlineData(14, TradingDays.Saturday)]
    [InlineData(15, TradingDays.Sunday)]
    [InlineData(16, TradingDays.Monday)]
    public void A_date_maps_to_its_weekday_flag(int day, TradingDays expected) =>
        Assert.Equal(expected, ScheduleRules.DayOf(new DateOnly(2026, 11, day)));

    [Fact]
    public void A_scheduled_start_is_allowed_on_a_trading_day_in_its_days()
    {
        var v = ScheduleRules.ScheduledStart(Make(), Wed, Normal(Wed));
        Assert.True(v.Allowed);
    }

    [Fact]
    public void A_strategy_the_trader_stopped_is_not_started_by_the_schedule()
    {
        var v = ScheduleRules.ScheduledStart(Make(manual: true), Wed, Normal(Wed));
        Assert.False(v.Allowed);
        Assert.Equal(ScheduleRules.ManualStop, v.Reason);
    }

    [Fact]
    public void A_day_outside_the_chosen_days_is_refused_even_when_the_market_is_open()
    {
        var v = ScheduleRules.ScheduledStart(Make(days: TradingDays.Monday), Wed, Normal(Wed));
        Assert.False(v.Allowed);
        Assert.Equal(ScheduleRules.NotToday, v.Reason);
    }

    [Fact]
    public void A_weekend_the_trader_chose_still_does_not_start_a_cash_market_strategy()
    {
        var v = ScheduleRules.ScheduledStart(Make(days: TradingDays.Weekdays | TradingDays.Saturday), Sat, Normal(Sat));
        Assert.False(v.Allowed);
        Assert.Equal(ScheduleRules.Weekend, v.Reason);
        Assert.Contains("weekend", v.Message);
    }

    [Fact]
    public void A_holiday_is_named_as_a_holiday()
    {
        var v = ScheduleRules.ScheduledStart(Make(), Wed, Holiday(Wed));
        Assert.False(v.Allowed);
        Assert.Equal(ScheduleRules.Holiday, v.Reason);
    }

    [Fact]
    public void A_special_weekend_session_counts_as_a_trading_day()
    {
        var muhurat = MarketCalendar.Sessions(Sat,
            new CalendarDay(Sat, "Muhurat", "SPECIAL_SESSION", [new CalendarExchange("NSE", true, MarketCalendar.MidnightIst(Sat) + 64_800_000, MarketCalendar.MidnightIst(Sat) + 68_400_000)]),
            MarketCalendar.DefaultOffsets);
        Assert.True(ScheduleRules.ScheduledStart(Make(days: TradingDays.Saturday), Sat, muhurat).Allowed);
    }

    [Fact]
    public void A_crypto_strategy_is_never_refused_for_the_calendar()
    {
        Assert.True(ScheduleRules.ScheduledStart(Make("CRYPTO", days: TradingDays.Weekdays | TradingDays.Saturday | TradingDays.Sunday), Sun, Normal(Sun)).Allowed);
        Assert.True(ScheduleRules.ScheduledStart(Make("CRYPTO"), Wed, Holiday(Wed)).Allowed);
    }

    [Fact]
    public void No_chosen_days_means_every_day()
    {
        Assert.True(ScheduleRules.ScheduledStart(Make("CRYPTO", days: TradingDays.None), Sun, Normal(Sun)).Allowed);
    }

    [Theory]
    [InlineData(9, 29, 59, false)]   // before the trader's start
    [InlineData(9, 30, 0, true)]
    [InlineData(12, 0, 0, true)]
    [InlineData(15, 0, 0, true)]     // the stop minute itself is inside
    [InlineData(15, 0, 1, false)]
    public void The_window_is_the_traders_start_to_stop(int h, int m, int s, bool inside) =>
        Assert.Equal(inside, ScheduleRules.WithinWindow(Make(), At(Wed, h, m, s), Normal(Wed)));

    [Theory]
    [InlineData(9, 0, 0, false)]     // trader's window opens at 08:00 but the exchange opens at 09:15
    [InlineData(9, 14, 59, false)]
    [InlineData(9, 15, 0, true)]
    [InlineData(15, 30, 0, true)]
    [InlineData(15, 30, 1, false)]   // the exchange closes at 15:30 though the trader's window runs to 16:00
    public void The_window_is_cut_down_to_the_exchange_session(int h, int m, int s, bool inside) =>
        Assert.Equal(inside, ScheduleRules.WithinWindow(Make(start: "08:00", stop: "16:00"), At(Wed, h, m, s), Normal(Wed)));

    [Fact]
    public void A_closed_exchange_has_no_window()
    {
        Assert.False(ScheduleRules.WithinWindow(Make(), At(Sat, 10, 0), Normal(Sat)));
        Assert.False(ScheduleRules.WithinWindow(Make(), At(Wed, 10, 0), Holiday(Wed)));
    }

    [Fact]
    public void A_blank_start_is_accepted_only_for_crypto()
    {
        Assert.False(ScheduleRules.WithinWindow(Make(start: null), At(Wed, 10, 0), Normal(Wed)));
        Assert.True(ScheduleRules.WithinWindow(Make("CRYPTO", start: null, stop: null), At(Sat, 3, 0), Normal(Sat)));
    }

    [Fact]
    public void A_crypto_strategy_with_no_stop_is_never_outside_its_window_not_even_in_the_last_second()
    {
        var s = Make("CRYPTO", start: null, stop: null);
        Assert.True(ScheduleRules.WithinWindow(s, At(Wed, 23, 59, 59).AddMilliseconds(500), Normal(Wed)));
        Assert.True(ScheduleRules.WithinWindow(s, At(Wed, 0, 0, 0), Normal(Wed)));
    }

    [Fact]
    public void A_commodity_evening_session_on_an_equity_holiday_is_a_window()
    {
        var evening = MarketCalendar.Sessions(Wed,
            new CalendarDay(Wed, "Equity holiday", "TRADING_HOLIDAY", [new CalendarExchange("MCX", true, MarketCalendar.MidnightIst(Wed) + 61_200_000, MarketCalendar.MidnightIst(Wed) + 84_600_000)]),
            MarketCalendar.DefaultOffsets);
        var mcx = Make("MCX", start: "17:00", stop: "23:30");
        Assert.True(ScheduleRules.WithinWindow(mcx, At(Wed, 18, 0), evening));
        Assert.False(ScheduleRules.WithinWindow(mcx, At(Wed, 10, 0), evening));
        Assert.False(ScheduleRules.WithinWindow(Make(), At(Wed, 10, 0), evening));
    }

    [Fact]
    public void The_enforcer_stops_a_running_strategy_when_its_exchange_is_closed_for_the_day()
    {
        var weekend = ScheduleRules.Enforce(Make(running: true), At(Sat, 10, 0), Normal(Sat));
        Assert.Equal((EnforcerKind.Stop, ScheduleRules.Weekend), (weekend.Kind, weekend.Reason));
        var holiday = ScheduleRules.Enforce(Make(running: true), At(Wed, 10, 0), Holiday(Wed));
        Assert.Equal((EnforcerKind.Stop, ScheduleRules.Holiday), (holiday.Kind, holiday.Reason));
    }

    [Fact]
    public void The_enforcer_leaves_alone_what_is_not_running_not_scheduled_or_crypto()
    {
        Assert.Equal(EnforcerKind.None, ScheduleRules.Enforce(Make(running: false), At(Sat, 10, 0), Normal(Sat)).Kind);
        Assert.Equal(EnforcerKind.None, ScheduleRules.Enforce(Make(running: true, scheduled: false), At(Sat, 10, 0), Normal(Sat)).Kind);
        Assert.Equal(EnforcerKind.None, ScheduleRules.Enforce(Make("CRYPTO", running: true), At(Sat, 10, 0), Normal(Sat)).Kind);
    }

    [Fact]
    public void The_enforcer_never_stops_a_strategy_for_the_time_of_day()
    {
        // 16:30 on a trading day, after the 15:00 stop and the 15:30 close: the scheduled stop decides that, not the enforcer.
        Assert.Equal(EnforcerKind.None, ScheduleRules.Enforce(Make(running: true), At(Wed, 16, 30), Normal(Wed)).Kind);
    }

    [Fact]
    public void The_enforcer_resumes_a_paused_strategy_once_the_exchange_trades_and_the_window_is_open()
    {
        var a = ScheduleRules.Enforce(Make(paused: ScheduleRules.Weekend), At(Mon, 10, 0), Normal(Mon));
        Assert.Equal((EnforcerKind.Resume, ScheduleRules.Weekend), (a.Kind, a.Reason));
    }

    [Theory]
    [InlineData("before_window")]
    [InlineData("manual")]
    [InlineData("not_a_day")]
    [InlineData("running")]
    [InlineData("pause_not_from_calendar")]
    public void The_enforcer_does_not_resume_unless_every_condition_holds(string case_)
    {
        var s = case_ switch
        {
            "manual" => Make(paused: ScheduleRules.Weekend, manual: true),
            "not_a_day" => Make(paused: ScheduleRules.Weekend, days: TradingDays.Tuesday),
            "running" => Make(paused: ScheduleRules.Weekend, running: true),
            "pause_not_from_calendar" => Make(paused: "kill_switch"),
            _ => Make(paused: ScheduleRules.Weekend),
        };
        var now = case_ == "before_window" ? At(Mon, 9, 20) : At(Mon, 10, 0);
        Assert.Equal(EnforcerKind.None, ScheduleRules.Enforce(s, now, Normal(Mon)).Kind);
    }

    [Fact]
    public void The_daily_check_stops_only_a_running_scheduled_strategy_on_a_closed_day()
    {
        Assert.Equal(EnforcerKind.Stop, ScheduleRules.DailyCheck(Make(running: true), Sat, Normal(Sat)).Kind);
        Assert.Equal(EnforcerKind.None, ScheduleRules.DailyCheck(Make(running: true), Wed, Normal(Wed)).Kind);
        Assert.Equal(EnforcerKind.None, ScheduleRules.DailyCheck(Make("CRYPTO", running: true), Sat, Normal(Sat)).Kind);
        Assert.Equal(EnforcerKind.None, ScheduleRules.DailyCheck(Make(running: false), Sat, Normal(Sat)).Kind);
        Assert.Equal(EnforcerKind.None, ScheduleRules.DailyCheck(Make(running: true, scheduled: false), Sat, Normal(Sat)).Kind);
    }
}
