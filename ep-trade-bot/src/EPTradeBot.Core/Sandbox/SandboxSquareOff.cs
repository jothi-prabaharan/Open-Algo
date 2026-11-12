using System.Globalization;

namespace EPTradeBot.Core.Sandbox;

/// <summary>The time each exchange's intraday (MIS) sandbox positions are closed, and whether it has come. OpenAlgo's sandbox square-off manager.</summary>
public static class SandboxSquareOff
{
    /// <summary>Reads HH:MM; anything else is the NSE default of 15:15, as OpenAlgo falls back.</summary>
    public static TimeOnly Parse(string? text)
    {
        var parts = (text ?? "").Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && h < 24 && m < 60
            ? new TimeOnly(h, m)
            : new TimeOnly(15, 15);
    }

    /// <summary>The square-off time for an exchange, or null for an exchange with none (nothing is closed there).</summary>
    public static TimeOnly? TimeFor(string exchange, string nseBse, string cdsBcd, string mcx, string ncdex) => exchange switch
    {
        "NSE" or "BSE" or "NFO" or "BFO" => Parse(nseBse),
        "CDS" or "BCD" => Parse(cdsBcd),
        "MCX" => Parse(mcx),
        "NCDEX" => Parse(ncdex),
        _ => null,
    };

    /// <summary>Due from the square-off minute onwards, for the rest of that day.</summary>
    public static bool IsDue(TimeOnly? squareOff, TimeOnly now) => squareOff is { } t && now >= t;
}
