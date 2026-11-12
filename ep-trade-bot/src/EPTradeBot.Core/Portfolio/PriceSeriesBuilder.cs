using System.Globalization;

namespace EPTradeBot.Core.Portfolio;

/// <summary>
/// Turns dated closes from Historify or a broker into the series the SIP and sector tools use, with the checks
/// OpenAlgo's portfolio loader makes: a session read twice keeps the last value, bars without a close are dropped,
/// every close must be positive, and a move large enough to be an unadjusted split or bonus is reported.
/// </summary>
public static class PriceSeriesBuilder
{
    /// <summary>A daily move above this is more likely an unadjusted corporate action than a market move (a 1:2 split is exactly 50 percent; NSE circuits stop at 20).</summary>
    public const double SplitThreshold = 0.35;

    public static PriceSeries Build(IEnumerable<(DateOnly Date, double? Close)> bars, string symbol, string start, string end, int minSessions = 2)
    {
        var last = new SortedDictionary<DateOnly, double>();
        foreach (var (date, close) in bars)
        {
            if (close is not { } c || double.IsNaN(c)) continue;
            last[date] = c;
        }

        if (last.Count == 0) throw new PriceDataException($"{symbol}: no rows survived timestamp/close parsing");
        if (last.Values.Any(v => !double.IsFinite(v) || v <= 0)) throw new PriceDataException("price matrix must contain only positive finite closes");
        if (last.Count < minSessions)
        {
            throw new PriceDataException($"only {last.Count} session(s) are common to all 1 symbols between {start} and {end}; a shorter window or a symbol with a later listing date is likely");
        }

        return new PriceSeries([.. last.Keys], [.. last.Values]);
    }

    /// <summary>The sessions whose close moved by more than <see cref="SplitThreshold"/>, worded as OpenAlgo words them: ('2024-01-05', -0.5).</summary>
    public static List<string> SplitWarnings(PriceSeries series)
    {
        var warnings = new List<string>();
        for (var i = 1; i < series.Count; i++)
        {
            var move = series.Closes[i] / series.Closes[i - 1] - 1.0;
            if (Math.Abs(move) > SplitThreshold)
                warnings.Add($"('{series.Dates[i].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}', {PythonFloat(move)})");
        }

        return warnings;
    }

    /// <summary>A double as Python prints it: the shortest text that reads back as the same number, with a decimal point.</summary>
    internal static string PythonFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains("Infinity", StringComparison.Ordinal) || text == "NaN" ? text : text + ".0";
    }
}
