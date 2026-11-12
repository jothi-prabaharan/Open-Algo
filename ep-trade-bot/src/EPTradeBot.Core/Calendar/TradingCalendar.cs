using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Calendar;

/// <summary>
/// Trading-day arithmetic from the exchange holiday list: answers "has a new week, month or quarter started" without remembering anything between runs. A special session (Muhurat trading on a Sunday)
/// is a trading day; a trading holiday is not; any other kind of entry leaves trading open. OpenAlgo's utils.trading_calendar.
/// </summary>
public sealed class TradingCalendar(IReadOnlyDictionary<int, (IReadOnlySet<DateOnly> Closed, IReadOnlySet<DateOnly> Special)> years)
{
    public static readonly string[] Periods = ["day", "week", "month", "quarter", "year"];

    /// <summary>The closed and special-session dates a holidays answer lists; empty sets (weekends only) when the answer failed.</summary>
    public static (IReadOnlySet<DateOnly> Closed, IReadOnlySet<DateOnly> Special) FromHolidays(JsonObject answer)
    {
        var closed = new HashSet<DateOnly>();
        var special = new HashSet<DateOnly>();
        if (answer["status"]?.GetValue<string>() != "error" && answer["data"] is JsonArray data)
            foreach (var entry in data.OfType<JsonObject>())
            {
                var raw = entry["date"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : null;
                if (string.IsNullOrEmpty(raw) || !DateOnly.TryParseExact(raw.Length > 10 ? raw[..10] : raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                var kind = (entry["holiday_type"] is JsonValue k && k.TryGetValue<string>(out var kt) ? kt : "").ToUpperInvariant();
                var closedExchanges = entry["closed_exchanges"] as JsonArray;
                if (kind == "SPECIAL_SESSION" || closedExchanges is null || closedExchanges.Count == 0) special.Add(day);
                else if (kind == "TRADING_HOLIDAY") closed.Add(day);
            }

        return (closed, special);
    }

    private (IReadOnlySet<DateOnly> Closed, IReadOnlySet<DateOnly> Special) Year(int year) => years.TryGetValue(year, out var y) ? y : (new HashSet<DateOnly>(), new HashSet<DateOnly>());

    public static int QuarterOf(DateOnly d) => (d.Month - 1) / 3 + 1;

    public bool IsTradingHoliday(DateOnly d) => Year(d.Year).Closed.Contains(d);

    public bool IsSpecialSession(DateOnly d) => Year(d.Year).Special.Contains(d);

    public bool IsTradingDay(DateOnly d) => IsSpecialSession(d) || (!IsTradingHoliday(d) && d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));

    public DateOnly PrevTradingDay(DateOnly d)
    {
        var cursor = d.AddDays(-1);
        for (var i = 0; i < 30; i++)
        {
            if (IsTradingDay(cursor)) return cursor;
            cursor = cursor.AddDays(-1);
        }

        return cursor;
    }

    public DateOnly NextTradingDay(DateOnly d)
    {
        var cursor = d.AddDays(1);
        for (var i = 0; i < 30; i++)
        {
            if (IsTradingDay(cursor)) return cursor;
            cursor = cursor.AddDays(1);
        }

        return cursor;
    }

    private static DateOnly Start(DateOnly d, string period) => period switch
    {
        "day" => d,
        "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
        "month" => new DateOnly(d.Year, d.Month, 1),
        "quarter" => new DateOnly(d.Year, 3 * (QuarterOf(d) - 1) + 1, 1),
        "year" => new DateOnly(d.Year, 1, 1),
        _ => throw new ArgumentException($"Unknown period '{period}'; expected one of ({string.Join(", ", Periods.Select(p => $"'{p}'"))})"),
    };

    private static DateOnly End(DateOnly d, string period) => period switch
    {
        "day" => d,
        "week" => Start(d, "week").AddDays(6),
        "month" => new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays(-1),
        "quarter" => new DateOnly(d.Year, 3 * QuarterOf(d), 1).AddMonths(1).AddDays(-1),
        "year" => new DateOnly(d.Year, 12, 31),
        _ => throw new ArgumentException($"Unknown period '{period}'; expected one of ({string.Join(", ", Periods.Select(p => $"'{p}'"))})"),
    };

    public DateOnly FirstTradingDayOf(DateOnly d, string period)
    {
        var cursor = Start(d, period);
        var end = End(d, period);
        while (cursor <= end && !IsTradingDay(cursor)) cursor = cursor.AddDays(1);
        return cursor;
    }

    public DateOnly LastTradingDayOf(DateOnly d, string period)
    {
        var cursor = End(d, period);
        var start = Start(d, period);
        while (cursor >= start && !IsTradingDay(cursor)) cursor = cursor.AddDays(-1);
        return cursor;
    }

    public bool IsFirstTradingDayOf(DateOnly d, string period) => IsTradingDay(d) && d == FirstTradingDayOf(d, period);

    public bool IsLastTradingDayOf(DateOnly d, string period) => IsTradingDay(d) && d == LastTradingDayOf(d, period);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Everything a workflow might branch on for one date.</summary>
    public JsonObject Describe(DateOnly d) => new()
    {
        ["date"] = Iso(d), ["is_trading_day"] = IsTradingDay(d), ["is_trading_holiday"] = IsTradingHoliday(d), ["is_weekend"] = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
        ["is_special_session"] = IsSpecialSession(d), ["weekday"] = d.DayOfWeek.ToString(), ["weekday_num"] = d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek, ["day"] = d.Day, ["month"] = d.Month,
        ["quarter"] = QuarterOf(d), ["year"] = d.Year, ["week_of_year"] = System.Globalization.ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)), ["day_of_year"] = d.DayOfYear,
        ["is_new_day"] = IsFirstTradingDayOf(d, "day"), ["is_new_week"] = IsFirstTradingDayOf(d, "week"), ["is_new_month"] = IsFirstTradingDayOf(d, "month"), ["is_new_quarter"] = IsFirstTradingDayOf(d, "quarter"),
        ["is_new_year"] = IsFirstTradingDayOf(d, "year"), ["is_last_day_of_week"] = IsLastTradingDayOf(d, "week"), ["is_last_day_of_month"] = IsLastTradingDayOf(d, "month"),
        ["is_last_day_of_quarter"] = IsLastTradingDayOf(d, "quarter"), ["is_last_day_of_year"] = IsLastTradingDayOf(d, "year"), ["prev_trading_day"] = Iso(PrevTradingDay(d)), ["next_trading_day"] = Iso(NextTradingDay(d)),
        ["first_trading_day_of_week"] = Iso(FirstTradingDayOf(d, "week")), ["first_trading_day_of_month"] = Iso(FirstTradingDayOf(d, "month")), ["first_trading_day_of_quarter"] = Iso(FirstTradingDayOf(d, "quarter")),
        ["last_trading_day_of_week"] = Iso(LastTradingDayOf(d, "week")), ["last_trading_day_of_month"] = Iso(LastTradingDayOf(d, "month")), ["last_trading_day_of_quarter"] = Iso(LastTradingDayOf(d, "quarter")),
    };
}
