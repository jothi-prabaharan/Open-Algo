namespace EPTradeBot.Core.Time;

/// <summary>India Standard Time helpers. IST has no daylight saving, so the offset is fixed.</summary>
public static class Ist
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    public static readonly TimeZoneInfo Zone = ResolveZone();

    /// <summary>The IST wall-clock time of a UTC instant, as an unspecified-kind DateTime.</summary>
    public static DateTime FromUtc(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
        return DateTime.SpecifyKind(utc + Offset, DateTimeKind.Unspecified);
    }

    /// <summary>The trading day (IST calendar date) a UTC instant falls on.</summary>
    public static DateOnly TradingDay(DateTime utc) => DateOnly.FromDateTime(FromUtc(utc));

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        }

        return TimeZoneInfo.CreateCustomTimeZone("IST", Offset, "India Standard Time", "IST");
    }
}
