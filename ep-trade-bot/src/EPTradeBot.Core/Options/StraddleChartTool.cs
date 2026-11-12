using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// The dynamic at-the-money straddle through time: for each underlying candle the strike nearest the close, its call and put closes at that moment, their sum, and the synthetic future.
/// OpenAlgo's get_straddle_chart_data with the strike list, history and quote passed in.
/// </summary>
public static class StraddleChartTool
{
    public delegate (bool Ok, JsonObject Response, int Code) HistoryFetch(string symbol, string exchange, string interval, string start, string end);

    public delegate (bool Ok, JsonObject Response, int Code) QuoteFetch(string symbol, string exchange);

    private static (bool Ok, JsonObject Response, int Code) Fail(string message, int code) => (false, new JsonObject { ["status"] = "error", ["message"] = message }, code);

    /// <summary>Calendar days from now to 15:30 on the expiry day, never negative; 0 when the date cannot be read.</summary>
    public static int DaysToExpiry(string expiry, DateTime nowIst)
    {
        var text = expiry.ToUpperInvariant();
        if (text.Length != 7 || !DateTime.TryParseExact(text, "ddMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return 0;
        var close = date.Date.AddHours(15).AddMinutes(30);
        return Math.Max(0, (int)Math.Floor((close - nowIst).TotalDays));
    }

    public static (bool Ok, JsonObject Response, int Code) Compute(
        string underlying, string exchange, string expiryDate, string interval, int days, IReadOnlyList<double> availableStrikes, string underlyingQuoteSymbol,
        HistoryFetch history, QuoteFetch quote, DateTime nowIst)
    {
        var (start, end) = TradingWindow.Resolve(days, nowIst);
        var baseSymbol = underlying.ToUpperInvariant();
        var quoteExchange = VolSurfaceTool.QuoteExchange(baseSymbol, exchange);
        var optionsExchange = VolSurfaceTool.OptionExchange(quoteExchange);
        var expiry = expiryDate.ToUpperInvariant();
        if (availableStrikes.Count == 0) return Fail($"No strikes found for {baseSymbol} {expiryDate} on {optionsExchange}", 404);

        var (okU, respU, _) = history(underlyingQuoteSymbol, quoteExchange, interval, start, end);
        if (!okU) return Fail($"Failed to fetch underlying history: {OptionChainReading.Text(respU, "message") ?? "Unknown error"}", 400);
        if ((respU["data"] as JsonArray ?? []).Count == 0) return Fail("No underlying history data available", 404);
        var candles = StrategyChartSupport.Candles((JsonArray)respU["data"]!);
        if (candles is null) return Fail("Failed to parse underlying timestamps", 500);

        var rows = new List<(long Time, double Spot, double? Atm)>();
        foreach (var (time, row) in candles)
        {
            if (row["close"] is not JsonValue cv || !TryDouble(cv, out var close)) return Fail("'close'", 500);
            rows.Add((time, close, VolSurfaceTool.AtmStrike(close, availableStrikes)));
        }

        var unique = rows.Where(r => r.Atm is not null).Select(r => r.Atm!.Value).Distinct().OrderBy(s => s).ToList();
        if (unique.Count == 0) return Fail("Could not determine any ATM strikes", 400);

        var strikeData = new Dictionary<double, (Dictionary<long, double> Ce, Dictionary<long, double> Pe)>();
        foreach (var strike in unique)
        {
            Dictionary<long, double> Closes(string type)
            {
                var map = new Dictionary<long, double>();
                var (ok, resp, _) = history(VolSurfaceTool.OptionSymbol(baseSymbol, expiry, strike, type), optionsExchange, interval, start, end);
                if (ok && resp["data"] is JsonArray { Count: > 0 } data && StrategyChartSupport.Candles(data) is { } cs)
                    foreach (var (time, row) in cs)
                        if (row["close"] is JsonValue v && TryDouble(v, out var close)) map[time] = close;
                return map;
            }

            var ce = Closes("CE");
            var pe = Closes("PE");
            strikeData[strike] = (ce, pe);
        }

        var series = new List<JsonObject>();
        foreach (var (time, spot, atm) in rows)
        {
            if (atm is not { } strike || !strikeData.TryGetValue(strike, out var sd)) continue;
            if (!sd.Ce.TryGetValue(time, out var cePrice) || !sd.Pe.TryGetValue(time, out var pePrice)) continue;
            series.Add(new JsonObject
            {
                ["time"] = time, ["spot"] = PyNumber.Round(spot, 2), ["atm_strike"] = strike, ["ce_price"] = PyNumber.Round(cePrice, 2), ["pe_price"] = PyNumber.Round(pePrice, 2),
                ["straddle"] = PyNumber.Round(cePrice + pePrice, 2), ["synthetic_future"] = PyNumber.Round(strike + cePrice - pePrice, 2),
            });
        }

        if (series.Count == 0) return Fail("No straddle data available (option history may be missing)", 404);
        series = StrategyChartSupport.CapLastTradingDates(series, days);

        var (okQ, respQ, _) = quote(underlyingQuoteSymbol, quoteExchange);
        var ltp = okQ && respQ["data"] is JsonObject d ? OptionChainReading.Num(d, "ltp") : 0;
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["underlying"] = baseSymbol, ["underlying_ltp"] = ltp, ["expiry_date"] = expiry, ["interval"] = interval, ["days_to_expiry"] = DaysToExpiry(expiryDate, nowIst),
                ["series"] = StrategyChartSupport.ToArray(series),
            },
        }, 200);
    }

    private static bool TryDouble(JsonValue v, out double d)
    {
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        d = 0;
        return false;
    }
}
