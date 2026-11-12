using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// The implied volatility and Greeks of the at-the-money call and put through the session, candle by candle, from each option's close and the underlying's close at the same moment.
/// OpenAlgo's get_iv_chart_data and get_default_symbols with strikes, history and quotes passed in. This is the page the tools menu calls Option Greeks.
/// </summary>
public static class IvChartTool
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    private static (bool Ok, JsonObject Response, int Code) Fail(string message, int code) => (false, new JsonObject { ["status"] = "error", ["message"] = message }, code);

    /// <summary>Years and days from a candle's moment (Indian wall clock) to the expiry; zero once reached, and never under 0.0001 years.</summary>
    public static (double Years, double Days) TimeToExpiryAt(DateTime candle, DateTime expiry)
    {
        if (expiry <= candle) return (0.0, 0.0);
        var days = (expiry - candle).TotalSeconds / (60 * 60 * 24);
        var years = days / 365.0;
        if (years < 0.0001) { years = 0.0001; days = years * 365.0; }
        return (years, days);
    }

    private static bool TryNumber(JsonNode? n, out double d)
    {
        d = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        return false;
    }

    private static List<JsonObject> IvSeries(List<(long Time, JsonObject Row)> option, List<(long Time, JsonObject Row)> underlying, double strike, DateTime expiry, bool call, double rate)
    {
        var under = underlying.ToDictionary(c => c.Time, c => c.Row);
        var result = new List<JsonObject>();
        foreach (var (time, row) in option.Where(c => under.ContainsKey(c.Time)))
        {
            TryNumber(row["close"], out var optionClose);
            TryNumber(under[time]["close"], out var underlyingClose);
            var candle = DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime.Add(IstOffset);
            var (years, _) = TimeToExpiryAt(DateTime.SpecifyKind(candle, DateTimeKind.Unspecified), expiry);
            double? iv = null, delta = null, gamma = null, theta = null, vega = null;
            if (years > 0 && optionClose > 0 && underlyingClose > 0)
            {
                var sigma = Black76.ImpliedVolatility(optionClose, underlyingClose, strike, rate, years, call, out _);
                if (sigma is { } s)
                {
                    iv = PyNumber.Round(s * 100.0, 2);
                    if (s > 0)
                    {
                        delta = PyNumber.Round(Black76.Delta(call, underlyingClose, strike, years, rate, s), 4);
                        gamma = PyNumber.Round(Black76.Gamma(underlyingClose, strike, years, rate, s), 6);
                        theta = PyNumber.Round(Black76.Theta(call, underlyingClose, strike, years, rate, s), 4);
                        vega = PyNumber.Round(Black76.Vega(underlyingClose, strike, years, rate, s), 4);
                    }
                }
            }

            result.Add(new JsonObject
            {
                ["time"] = time, ["iv"] = iv, ["delta"] = delta, ["gamma"] = gamma, ["theta"] = theta, ["vega"] = vega, ["option_price"] = optionClose, ["underlying_price"] = underlyingClose,
            });
        }

        return result;
    }

    public static (bool Ok, JsonObject Response, int Code) Compute(
        string underlying, string exchange, string expiryDate, string interval, int days, IReadOnlyList<double> availableStrikes,
        StraddleChartTool.HistoryFetch history, StraddleChartTool.QuoteFetch quote, DateTime nowIst)
    {
        var (start, end) = TradingWindow.Resolve(days, nowIst);
        var baseSymbol = underlying.ToUpperInvariant();
        var quoteExchange = VolSurfaceTool.QuoteExchange(baseSymbol, exchange);
        var optionsExchange = VolSurfaceTool.OptionExchange(quoteExchange);
        var expiry = expiryDate.ToUpperInvariant();

        var (okQuote, quoteResponse, quoteCode) = quote(baseSymbol, quoteExchange);
        if (!okQuote) return Fail($"Failed to fetch underlying quote: {OptionChainReading.Text(quoteResponse, "message") ?? "Unknown error"}", quoteCode);
        var ltp = quoteResponse["data"] is JsonObject qd ? OptionChainReading.Num(qd, "ltp") : 0;
        if (ltp == 0) return Fail("Could not get underlying LTP", 400);
        if (availableStrikes.Count == 0) return Fail($"No strikes found for {baseSymbol} {expiryDate} on {optionsExchange}", 404);
        if (VolSurfaceTool.AtmStrike(ltp, availableStrikes) is not { } atm) return Fail("Could not determine ATM strike", 400);

        var ceSymbol = VolSurfaceTool.OptionSymbol(baseSymbol, expiry, atm, "CE");
        var peSymbol = VolSurfaceTool.OptionSymbol(baseSymbol, expiry, atm, "PE");
        var (_, expiryAt, strike, _) = OptionSymbols.Parse(ceSymbol, optionsExchange);

        var (okU, respU, _) = history(baseSymbol, quoteExchange, interval, start, end);
        var (okCe, respCe, _) = history(ceSymbol, optionsExchange, interval, start, end);
        var (okPe, respPe, _) = history(peSymbol, optionsExchange, interval, start, end);
        if (!okU) return Fail($"Failed to fetch underlying history: {OptionChainReading.Text(respU, "message") ?? "Unknown error"}", 400);
        var uData = respU["data"] as JsonArray ?? [];
        if (uData.Count == 0) return Fail("No underlying history data available for today", 404);
        var under = StrategyChartSupport.Candles(uData);
        if (under is null) return Fail("Failed to parse underlying timestamps", 500);

        var results = new List<(JsonObject Entry, List<JsonObject> Series)>();
        foreach (var (ok, resp, symbol, type, call) in new[] { (okCe, respCe, ceSymbol, "CE", true), (okPe, respPe, peSymbol, "PE", false) })
        {
            if (!ok || resp["data"] is not JsonArray { Count: > 0 } data || StrategyChartSupport.Candles(data) is not { } candles) continue;
            results.Add((new JsonObject { ["symbol"] = symbol, ["option_type"] = type, ["strike"] = strike }, IvSeries(candles, under, strike, expiryAt, call, 0.0)));
        }

        if (results.Count == 0) return Fail("No option history data available for today", 404);
        var series = new JsonArray();
        foreach (var (entry, rows) in results)
        {
            entry["iv_data"] = StrategyChartSupport.ToArray(StrategyChartSupport.CapLastTradingDates(rows, days));
            series.Add(entry);
        }

        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["underlying"] = baseSymbol, ["underlying_ltp"] = ltp, ["atm_strike"] = atm, ["ce_symbol"] = ceSymbol, ["pe_symbol"] = peSymbol, ["interval"] = interval, ["series"] = series,
            },
        }, 200);
    }

    /// <summary>The at-the-money call and put names for display.</summary>
    public static (bool Ok, JsonObject Response, int Code) DefaultSymbols(string underlying, string exchange, string expiryDate, IReadOnlyList<double> availableStrikes, StraddleChartTool.QuoteFetch quote)
    {
        var baseSymbol = underlying.ToUpperInvariant();
        var quoteExchange = VolSurfaceTool.QuoteExchange(baseSymbol, exchange);
        var optionsExchange = VolSurfaceTool.OptionExchange(quoteExchange);
        var expiry = expiryDate.ToUpperInvariant();
        var (ok, response, code) = quote(baseSymbol, quoteExchange);
        if (!ok) return Fail("Failed to fetch underlying quote", code);
        var ltp = response["data"] is JsonObject d ? OptionChainReading.Num(d, "ltp") : 0;
        if (ltp == 0) return Fail("Could not get underlying LTP", 400);
        if (availableStrikes.Count == 0) return Fail($"No strikes found for {baseSymbol} {expiryDate}", 404);
        if (VolSurfaceTool.AtmStrike(ltp, availableStrikes) is not { } atm) return Fail("Could not determine ATM strike", 400);
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["ce_symbol"] = VolSurfaceTool.OptionSymbol(baseSymbol, expiry, atm, "CE"), ["pe_symbol"] = VolSurfaceTool.OptionSymbol(baseSymbol, expiry, atm, "PE"), ["atm_strike"] = atm,
                ["exchange"] = optionsExchange, ["underlying_ltp"] = ltp,
            },
        }, 200);
    }
}
