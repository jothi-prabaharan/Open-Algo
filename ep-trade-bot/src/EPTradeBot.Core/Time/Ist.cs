namespace EPTradeBot.Core.Time;

/// <summary>India Standard Time helpers. IST has no daylight saving, so the offset is fixed.</summary>
public static class Ist
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    public static readonly TimeZoneInfo Zone = ResolveZone();

    /// <summary>The IST wall-clock time of a UTC instant, as an unspecified-kind DateTime.</summary>
    /// <summary>An Indian wall-clock time as UTC.</summary>
    public static DateTime ToUtc(DateTime ist) => DateTime.SpecifyKind(ist - Offset, DateTimeKind.Utc);

    public static DateTime FromUtc(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
        return DateTime.SpecifyKind(utc + Offset, DateTimeKind.Unspecified);
    }

    /// <summary>The trading day (IST calendar date) a UTC instant falls on.</summary>
    public static DateOnly TradingDay(DateTime utc) => DateOnly.FromDateTime(FromUtc(utc));

    /// <summary>
    /// The most recent session boundary at or before <paramref name="utcNow"/>, in UTC: the IST
    /// wall-clock <paramref name="expiry"/> (03:00 by default, when Indian broker sessions end)
    /// today, or yesterday when today's has not come yet. Port of OpenAlgo's
    /// sandbox/session_boundary.last_session_expiry_utc.
    /// </summary>
    public static DateTime LastSessionExpiryUtc(DateTime utcNow, TimeOnly? expiry = null)
    {
        var local = FromUtc(utcNow);
        var boundary = local.Date + (expiry ?? new TimeOnly(3, 0)).ToTimeSpan();
        if (local < boundary) boundary = boundary.AddDays(-1);
        return DateTime.SpecifyKind(boundary - Offset, DateTimeKind.Utc);
    }

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        }

        return TimeZoneInfo.CreateCustomTimeZone("IST", Offset, "India Standard Time", "IST");
    }
}
