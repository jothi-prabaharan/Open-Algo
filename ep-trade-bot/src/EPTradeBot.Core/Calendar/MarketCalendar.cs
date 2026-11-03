namespace EPTradeBot.Core.Calendar;

/// <summary>One exchange's row on a calendar entry: closed, or open for a window in epoch milliseconds.</summary>
public sealed record CalendarExchange(string Exchange, bool IsOpen, long? StartTime, long? EndTime);

/// <summary>A calendar entry (OpenAlgo's market_holidays row with its market_holiday_exchanges rows, in stored order).</summary>
public sealed record CalendarDay(DateOnly Date, string Description, string HolidayType, IReadOnlyList<CalendarExchange> Exchanges);

/// <summary>An exchange's session on one date, in epoch milliseconds.</summary>
public sealed record ExchangeSession(string Exchange, long? StartTime, long? EndTime);

/// <summary>
/// Which exchanges trade on a date, and when. Port of OpenAlgo's
/// database/market_calendar_db.get_market_timings_for_date. Pure: the calendar entry for the
/// date and the exchanges' session offsets come in as arguments.
/// </summary>
public static class MarketCalendar
{
    /// <summary>OpenAlgo's SUPPORTED_EXCHANGES, in its order (the order of a normal day's answer).</summary>
    public static readonly IReadOnlyList<string> Exchanges = ["NSE", "BSE", "NFO", "BFO", "MCX", "BCD", "CDS", "NCO", "CRYPTO"];

    /// <summary>The exchanges that trade every day of the week.</summary>
    public static readonly IReadOnlyList<string> AllWeekExchanges = ["CRYPTO"];

    public static readonly DateOnly FirstDate = new(2020, 1, 1);
    public static readonly DateOnly LastDate = new(2050, 12, 31);

    /// <summary>
    /// OpenAlgo's DEFAULT_MARKET_TIMINGS, in milliseconds from midnight IST. F&amp;O closes at 15:40,
    /// after the equity cash segment, since SEBI's Closing Auction Session (August 2026).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (long Start, long End)> DefaultOffsets = new Dictionary<string, (long, long)>
    {
        ["NSE"] = (33_300_000, 55_800_000), ["BSE"] = (33_300_000, 55_800_000),
        ["NFO"] = (33_300_000, 56_400_000), ["BFO"] = (33_300_000, 56_400_000),
        ["CDS"] = (32_400_000, 61_200_000), ["BCD"] = (32_400_000, 61_200_000),
        ["MCX"] = (32_400_000, 86_100_000), ["NCO"] = (32_400_000, 86_100_000),
        ["CRYPTO"] = (0, 86_399_000),
    };

    /// <summary>Midnight IST of a date, in epoch milliseconds.</summary>
    public static long MidnightIst(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();

    /// <summary>
    /// The sessions on <paramref name="date"/>. A special session (Muhurat) answers its own windows;
    /// a settlement holiday trades normal hours; a trading holiday answers only the exchanges it
    /// lists as open; a weekend with no entry trades only the all-week exchanges.
    /// <paramref name="offsets"/> are the stored session offsets, which win over the defaults.
    /// </summary>
    public static IReadOnlyList<ExchangeSession> Sessions(DateOnly date, CalendarDay? entry, IReadOnlyDictionary<string, (long Start, long End)> offsets)
    {
        var midnight = MidnightIst(date);
        if (entry is not null)
        {
            if (entry.HolidayType == "SETTLEMENT_HOLIDAY") return Normal(Exchanges, midnight, offsets);

            // Keyed by exchange in first-seen order, as OpenAlgo's dict is: a later row for the same exchange replaces the window.
            var open = new List<string>();
            var windows = new Dictionary<string, ExchangeSession>();
            foreach (var e in entry.Exchanges.Where(e => e.IsOpen))
            {
                if (!windows.ContainsKey(e.Exchange)) open.Add(e.Exchange);
                // The stored window as it is, empty ends included, as OpenAlgo answers it.
                windows[e.Exchange] = new ExchangeSession(e.Exchange, e.StartTime, e.EndTime);
            }

            return open.Select(x => windows[x]).ToList();
        }

        return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? Normal(AllWeekExchanges, midnight, offsets)
            : Normal(Exchanges, midnight, offsets);
    }

    private static List<ExchangeSession> Normal(IEnumerable<string> exchanges, long midnight, IReadOnlyDictionary<string, (long Start, long End)> offsets)
    {
        var sessions = new List<ExchangeSession>();
        foreach (var x in exchanges)
        {
            if (!offsets.TryGetValue(x, out var o) && !DefaultOffsets.TryGetValue(x, out o)) continue;
            sessions.Add(new ExchangeSession(x, midnight + o.Start, midnight + o.End));
        }

        return sessions;
    }
}
