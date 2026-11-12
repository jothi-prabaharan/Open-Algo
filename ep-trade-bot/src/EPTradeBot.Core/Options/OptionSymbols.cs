using System.Globalization;
using System.Text.RegularExpressions;

namespace EPTradeBot.Core.Options;

/// <summary>Option symbols, expiries and the exchange cut-off times, as OpenAlgo's Greeks service reads them. All times are Indian wall-clock times.</summary>
public static partial class OptionSymbols
{
    private static readonly Dictionary<string, int> Months = new()
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAY"] = 5, ["JUN"] = 6, ["JUL"] = 7, ["AUG"] = 8, ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12,
    };

    /// <summary>A refused input; the message is the sentence OpenAlgo gives.</summary>
    public sealed class RefusedException(string message) : Exception(message);

    [GeneratedRegex(@"^([A-Z0-9&-]+?)(\d{2})([A-Z]{3})(\d{2})([\d.]+)(CE|PE)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Symbol();

    /// <summary>The cut-off, from an override ("15:30", "19:00") or the exchange's own.</summary>
    public static (int Hour, int Minute) ExpiryTime(string exchange, string? custom)
    {
        if (string.IsNullOrEmpty(custom)) return exchange switch { "MCX" => (23, 30), "CDS" => (12, 30), _ => (15, 30) };
        var parts = custom.Split(':');
        if (parts.Length != 2) throw new RefusedException($"Invalid expiry_time format: {custom}. Use HH:MM format (e.g., '15:30', '19:00')");
        if (!int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var hour))
            throw new RefusedException($"Failed to parse expiry_time '{custom}': invalid literal for int() with base 10: '{parts[0]}'");
        if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minute))
            throw new RefusedException($"Failed to parse expiry_time '{custom}': invalid literal for int() with base 10: '{parts[1]}'");
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            throw new RefusedException($"Invalid expiry_time values: {custom}. Hour must be 0-23, minute must be 0-59");
        return (hour, minute);
    }

    /// <summary>Splits NIFTY26NOV2624000CE into its underlying, expiry (an Indian wall-clock time), strike and CE or PE.</summary>
    public static (string Base, DateTime Expiry, double Strike, string Type) Parse(string symbol, string exchange, string? customExpiryTime = null)
    {
        try
        {
            var m = Symbol().Match(symbol.ToUpperInvariant());
            if (!m.Success) throw new RefusedException($"Invalid option symbol format: {symbol}");
            if (!Months.TryGetValue(m.Groups[3].Value, out var month)) throw new RefusedException($"Invalid month in option symbol: {symbol}");
            var (hour, minute) = ExpiryTime(exchange, customExpiryTime);
            DateTime expiry;
            try
            {
                expiry = new DateTime(2000 + int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), month, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), hour, minute, 0, DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new RefusedException("day is out of range for month");
            }

            if (!double.TryParse(m.Groups[5].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var strike))
                throw new RefusedException($"could not convert string to float: '{m.Groups[5].Value}'");
            return (m.Groups[1].Value, expiry, strike, m.Groups[6].Value);
        }
        catch (RefusedException e)
        {
            throw new RefusedException($"Failed to parse option symbol {symbol}: {e.Message}");
        }
    }
}
