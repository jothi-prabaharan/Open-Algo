using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Entities.Core;

namespace EPTradeBot.Core.Scheduling;

/// <summary>What the scheduler needs to know about a hosted strategy to decide anything (a copy, so the rules touch no entity).</summary>
public sealed record StrategySchedule(
    string Exchange,
    TimeOnly? StartIst,
    TimeOnly? StopIst,
    TradingDays Days,
    bool IsScheduled,
    bool IsEnabled,
    bool IsRunning,
    bool ManuallyStopped,
    string? PausedReason);

public enum EnforcerKind
{
    None = 0,
    Stop = 1,
    Resume = 2,
}

public sealed record EnforcerAction(EnforcerKind Kind, string? Reason = null);

public sealed record StartVerdict(bool Allowed, string? Reason, string Message);

/// <summary>
/// When a hosted strategy may run. Port of the scheduling rules in OpenAlgo's blueprints/python_strategy.py
/// (scheduled_start_strategy, is_within_schedule_time, market_hours_enforcer, daily trading-day check).
/// Pure: the sessions of the day come in as an argument, the clock as a value.
/// </summary>
public static class ScheduleRules
{
    public const string Crypto = "CRYPTO";

    public const string Weekend = "weekend";
    public const string Holiday = "holiday";
    public const string ManualStop = "manual_stop";
    public const string NotToday = "not_scheduled_today";

    /// <summary>The reasons a pause may carry, which the enforcer clears itself when the exchange trades again.</summary>
    public static readonly IReadOnlyList<string> ClearablePauses = [Weekend, Holiday, "before_market", "after_market"];

    public static TradingDays DayOf(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => TradingDays.Monday,
        DayOfWeek.Tuesday => TradingDays.Tuesday,
        DayOfWeek.Wednesday => TradingDays.Wednesday,
        DayOfWeek.Thursday => TradingDays.Thursday,
        DayOfWeek.Friday => TradingDays.Friday,
        DayOfWeek.Saturday => TradingDays.Saturday,
        _ => TradingDays.Sunday,
    };

    public static string NormalizeExchange(string? exchange) =>
        string.IsNullOrWhiteSpace(exchange) ? "NSE" : exchange.Trim().ToUpperInvariant();

    public static bool IsCrypto(string exchange) => NormalizeExchange(exchange) == Crypto;

    /// <summary>The exchange's session on a date, or null when it is closed all day (a weekend with no special session, a holiday).</summary>
    public static ExchangeSession? SessionOf(string exchange, IReadOnlyList<ExchangeSession> sessions)
    {
        var name = NormalizeExchange(exchange);
        return sessions.FirstOrDefault(s => string.Equals(s.Exchange, name, StringComparison.OrdinalIgnoreCase) && s.StartTime is not null && s.EndTime is not null);
    }

    /// <summary>Why an exchange is closed on a date: a weekend, or otherwise a holiday.</summary>
    public static string ClosedReason(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Weekend : Holiday;

    public static bool IsTradingDay(string exchange, IReadOnlyList<ExchangeSession> sessions) =>
        IsCrypto(exchange) || SessionOf(exchange, sessions) is not null;

    /// <summary>
    /// A scheduled start firing. Refused for a strategy the trader stopped by hand, on a day outside its days, and on a day its exchange is closed
    /// (a crypto strategy is never refused for the calendar).
    /// </summary>
    public static StartVerdict ScheduledStart(StrategySchedule s, DateOnly today, IReadOnlyList<ExchangeSession> sessions)
    {
        if (s.ManuallyStopped)
            return new StartVerdict(false, ManualStop, "You stopped this strategy yourself, so the schedule will not start it again until you start it.");
        if (s.Days != TradingDays.None && (s.Days & DayOf(today)) == 0)
            return new StartVerdict(false, NotToday, $"This strategy is not scheduled to run on a {today.DayOfWeek}.");
        if (!IsTradingDay(s.Exchange, sessions))
        {
            var why = ClosedReason(today);
            return new StartVerdict(false, why, why == Weekend
                ? $"The {NormalizeExchange(s.Exchange)} market is closed for the weekend, so the strategy was not started."
                : $"The {NormalizeExchange(s.Exchange)} market is closed today for a holiday, so the strategy was not started.");
        }

        return new StartVerdict(true, null, "Start.");
    }

    /// <summary>
    /// Whether an instant is inside the strategy's window today: the trader's start to stop, cut down to the exchange's session (so an MCX evening
    /// session on a day equity is shut, and a Muhurat session on a weekend, both count). A blank start is accepted only for crypto.
    /// </summary>
    public static bool WithinWindow(StrategySchedule s, DateTime nowUtc, IReadOnlyList<ExchangeSession> sessions)
    {
        var date = Time.Ist.TradingDay(nowUtc);
        var midnight = MarketCalendar.MidnightIst(date);
        var nowMs = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var crypto = IsCrypto(s.Exchange);

        long userStart;
        if (s.StartIst is { } start) userStart = midnight + (long)start.ToTimeSpan().TotalMilliseconds;
        else if (crypto) userStart = midnight;
        else return false;

        // Without a stop the window runs to the end of the day, so a 24/7 strategy is never outside it, not even in the last second.
        var userEnd = s.StopIst is { } stop ? midnight + (long)stop.ToTimeSpan().TotalMilliseconds : midnight + 86_400_000;

        long from = userStart, to = userEnd;
        if (!crypto)
        {
            if (SessionOf(s.Exchange, sessions) is not { } session) return false;
            from = Math.Max(userStart, session.StartTime!.Value);
            to = Math.Min(userEnd, session.EndTime!.Value);
            if (from > to) return false;
        }

        return from <= nowMs && nowMs <= to;
    }

    /// <summary>
    /// The per-minute check. It stops a running strategy whose exchange has no session today, and brings back one it paused, once the exchange
    /// trades again, if today is one of its days, the trader did not stop it and the time is inside its window. It never stops a strategy for the time
    /// of day: that is the scheduled stop's job, so a strategy left running across the bell on purpose is not fought.
    /// </summary>
    public static EnforcerAction Enforce(StrategySchedule s, DateTime nowUtc, IReadOnlyList<ExchangeSession> sessions)
    {
        if (!s.IsScheduled) return new EnforcerAction(EnforcerKind.None);
        var date = Time.Ist.TradingDay(nowUtc);

        if (IsTradingDay(s.Exchange, sessions))
        {
            var pausedByCalendar = s.PausedReason is not null && ClearablePauses.Contains(s.PausedReason);
            if (pausedByCalendar && !s.IsRunning && !s.ManuallyStopped
                && (s.Days == TradingDays.None || (s.Days & DayOf(date)) != 0)
                && WithinWindow(s, nowUtc, sessions))
                return new EnforcerAction(EnforcerKind.Resume, s.PausedReason);
            return new EnforcerAction(EnforcerKind.None);
        }

        return s.IsRunning ? new EnforcerAction(EnforcerKind.Stop, ClosedReason(date)) : new EnforcerAction(EnforcerKind.None);
    }

    /// <summary>
    /// The 00:01 IST check: a running scheduled strategy whose exchange is closed for the day is stopped and paused. A strategy on an exchange with a
    /// session today, and a crypto one always, is left alone.
    /// </summary>
    public static EnforcerAction DailyCheck(StrategySchedule s, DateOnly today, IReadOnlyList<ExchangeSession> sessions) =>
        s.IsScheduled && s.IsRunning && !IsTradingDay(s.Exchange, sessions)
            ? new EnforcerAction(EnforcerKind.Stop, ClosedReason(today))
            : new EnforcerAction(EnforcerKind.None);

    /// <summary>The Quartz day-of-week field for a set of days, or null for every day.</summary>
    public static string? QuartzDays(TradingDays days)
    {
        if (days == TradingDays.None) return null;
        var names = new List<string>();
        foreach (var (flag, name) in new[]
                 {
                     (TradingDays.Monday, "MON"), (TradingDays.Tuesday, "TUE"), (TradingDays.Wednesday, "WED"), (TradingDays.Thursday, "THU"),
                     (TradingDays.Friday, "FRI"), (TradingDays.Saturday, "SAT"), (TradingDays.Sunday, "SUN"),
                 })
        {
            if ((days & flag) != 0) names.Add(name);
        }

        return names.Count == 7 ? null : string.Join(',', names);
    }

    /// <summary>The Quartz cron for a time of day (IST) on the given days: seconds minutes hours day-of-month month day-of-week.</summary>
    public static string Cron(TimeOnly time, TradingDays days) =>
        $"0 {time.Minute} {time.Hour} ? * {QuartzDays(days) ?? "*"}";
}
