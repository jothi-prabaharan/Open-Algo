using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// A simulated intraday short straddle with automatic adjustments: sell the at-the-money call and put at the first candle, move to the new at-the-money strike when the spot has drifted
/// by the adjustment distance, close at the last candle, and carry the profit across days. OpenAlgo's get_custom_straddle_simulation with history and quotes passed in.
/// </summary>
public static class CustomStraddleTool
{
    private static (bool Ok, JsonObject Response, int Code) Fail(string message, int code) => (false, new JsonObject { ["status"] = "error", ["message"] = message }, code);

    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    private sealed record Candle(long Time, double Spot, double? Atm);

    public static (bool Ok, JsonObject Response, int Code) Compute(
        string underlying, string exchange, string expiryDate, string interval, int days, double adjustmentPoints, int lotSize, int lots,
        IReadOnlyList<double> availableStrikes, string underlyingQuoteSymbol, StraddleChartTool.HistoryFetch history, StraddleChartTool.QuoteFetch quote, DateTime nowIst)
    {
        var (start, end) = TradingWindow.Resolve(days, nowIst);
        var quantity = lotSize * lots;
        var baseSymbol = underlying.ToUpperInvariant();
        var quoteExchange = VolSurfaceTool.QuoteExchange(baseSymbol, exchange);
        var optionsExchange = VolSurfaceTool.OptionExchange(quoteExchange);
        var expiry = expiryDate.ToUpperInvariant();
        if (availableStrikes.Count == 0) return Fail($"No strikes found for {baseSymbol} {expiryDate} on {optionsExchange}", 404);

        var (okU, respU, _) = history(underlyingQuoteSymbol, quoteExchange, interval, start, end);
        if (!okU) return Fail($"Failed to fetch underlying history: {OptionChainReading.Text(respU, "message") ?? "Unknown"}", 400);
        if ((respU["data"] as JsonArray ?? []).Count == 0) return Fail("No underlying history data", 404);
        var raw = StrategyChartSupport.Candles((JsonArray)respU["data"]!);
        if (raw is null) return Fail("Failed to parse timestamps", 500);

        var candles = new List<Candle>();
        foreach (var (time, row) in raw)
        {
            if (row["close"] is not JsonValue cv || !TryDouble(cv, out var close)) return Fail("'close'", 500);
            candles.Add(new Candle(time, close, VolSurfaceTool.AtmStrike(close, availableStrikes)));
        }

        var unique = candles.Where(c => c.Atm is not null).Select(c => c.Atm!.Value).Distinct().OrderBy(s => s).ToList();
        if (unique.Count == 0) return Fail("Could not determine ATM strikes", 400);

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

        static DateOnly DayOf(long t) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.Add(IstOffset));
        var byDay = new SortedDictionary<DateOnly, List<Candle>>();
        foreach (var c in candles)
        {
            var day = DayOf(c.Time);
            if (!byDay.TryGetValue(day, out var list)) byDay[day] = list = [];
            list.Add(c);
        }

        double cumulative = 0.0;
        var totalAdjustments = 0;
        var pnlSeries = new List<JsonObject>();
        var trades = new JsonArray();
        var tradingDays = byDay.Keys.ToList();
        tradingDays = tradingDays.Skip(Math.Max(0, tradingDays.Count - Math.Max(1, days))).ToList();

        double? Price(Dictionary<long, double> map, long t) => map.TryGetValue(t, out var v) ? v : null;

        foreach (var day in tradingDays)
        {
            var dayCandles = byDay[day];
            double? entryStrike = null;
            double entryCe = 0, entryPe = 0;
            double dayRealized = 0;
            var dayAdjustments = 0;
            double lastUnrealized = 0;

            for (var i = 0; i < dayCandles.Count; i++)
            {
                var c = dayCandles[i];
                if (c.Atm is not { } atm || !strikeData.ContainsKey(atm)) continue;
                var isLast = i == dayCandles.Count - 1;

                if (entryStrike is null)
                {
                    if (Price(strikeData[atm].Ce, c.Time) is not { } ceAt || Price(strikeData[atm].Pe, c.Time) is not { } peAt) continue;
                    entryStrike = atm;
                    entryCe = ceAt;
                    entryPe = peAt;
                    trades.Add(new JsonObject
                    {
                        ["time"] = c.Time, ["type"] = "ENTRY", ["strike"] = atm, ["ce_price"] = PyNumber.Round(ceAt, 2), ["pe_price"] = PyNumber.Round(peAt, 2),
                        ["straddle"] = PyNumber.Round(ceAt + peAt, 2), ["spot"] = PyNumber.Round(c.Spot, 2), ["leg_pnl"] = 0.0, ["cumulative_pnl"] = PyNumber.Round(cumulative, 2),
                    });
                }
                else if (Math.Abs(atm - entryStrike.Value) >= adjustmentPoints)
                {
                    var oldCe = Price(strikeData[entryStrike.Value].Ce, c.Time);
                    var oldPe = Price(strikeData[entryStrike.Value].Pe, c.Time);
                    var newCe = Price(strikeData[atm].Ce, c.Time);
                    var newPe = Price(strikeData[atm].Pe, c.Time);
                    if (oldCe is { } oc && oldPe is { } op && newCe is { } nc && newPe is { } np)
                    {
                        var legPnl = (entryCe - oc + (entryPe - op)) * quantity;
                        dayRealized += legPnl;
                        dayAdjustments++;
                        trades.Add(new JsonObject
                        {
                            ["time"] = c.Time, ["type"] = "ADJUSTMENT", ["old_strike"] = entryStrike.Value, ["strike"] = atm, ["exit_ce"] = PyNumber.Round(oc, 2), ["exit_pe"] = PyNumber.Round(op, 2),
                            ["exit_straddle"] = PyNumber.Round(oc + op, 2), ["ce_price"] = PyNumber.Round(nc, 2), ["pe_price"] = PyNumber.Round(np, 2), ["straddle"] = PyNumber.Round(nc + np, 2),
                            ["spot"] = PyNumber.Round(c.Spot, 2), ["leg_pnl"] = PyNumber.Round(legPnl, 2), ["cumulative_pnl"] = PyNumber.Round(cumulative + dayRealized, 2),
                        });
                        entryStrike = atm;
                        entryCe = nc;
                        entryPe = np;
                    }
                }

                var curCe = Price(strikeData[entryStrike!.Value].Ce, c.Time);
                var curPe = Price(strikeData[entryStrike.Value].Pe, c.Time);
                double unrealized;
                if (curCe is { } cc && curPe is { } cp)
                {
                    unrealized = (entryCe - cc + (entryPe - cp)) * quantity;
                    lastUnrealized = unrealized;
                }
                else unrealized = lastUnrealized;

                var totalPnl = cumulative + dayRealized + unrealized;
                var atmCe = Price(strikeData[atm].Ce, c.Time) ?? 0;
                var atmPe = Price(strikeData[atm].Pe, c.Time) ?? 0;
                var synthetic = atmCe != 0 && atmPe != 0 ? PyNumber.Round(atm + atmCe - atmPe, 2) : PyNumber.Round(c.Spot, 2);
                pnlSeries.Add(new JsonObject
                {
                    ["time"] = c.Time, ["pnl"] = PyNumber.Round(totalPnl, 2), ["spot"] = PyNumber.Round(c.Spot, 2), ["atm_strike"] = atm, ["entry_strike"] = entryStrike.Value,
                    ["ce_price"] = PyNumber.Round(atmCe, 2), ["pe_price"] = PyNumber.Round(atmPe, 2), ["straddle"] = PyNumber.Round(atmCe + atmPe, 2), ["synthetic_future"] = synthetic,
                    ["adjustments"] = totalAdjustments + dayAdjustments,
                });

                if (isLast)
                {
                    var exitCe = Price(strikeData[entryStrike.Value].Ce, c.Time);
                    var exitPe = Price(strikeData[entryStrike.Value].Pe, c.Time);
                    var legPnl = exitCe is { } ec && exitPe is { } ep ? (entryCe - ec + (entryPe - ep)) * quantity : lastUnrealized;
                    trades.Add(new JsonObject
                    {
                        ["time"] = c.Time, ["type"] = "EXIT", ["strike"] = entryStrike.Value, ["ce_price"] = PyNumber.Round(exitCe ?? 0, 2), ["pe_price"] = PyNumber.Round(exitPe ?? 0, 2),
                        ["straddle"] = PyNumber.Round((exitCe ?? 0) + (exitPe ?? 0), 2), ["spot"] = PyNumber.Round(c.Spot, 2), ["leg_pnl"] = PyNumber.Round(legPnl, 2),
                        ["cumulative_pnl"] = PyNumber.Round(cumulative + dayRealized + legPnl, 2),
                    });
                }
            }

            if (entryStrike is { } es)
            {
                var lastTime = dayCandles[^1].Time;
                var finalCe = Price(strikeData[es].Ce, lastTime);
                var finalPe = Price(strikeData[es].Pe, lastTime);
                var finalLeg = finalCe is { } fc && finalPe is { } fp ? (entryCe - fc + (entryPe - fp)) * quantity : lastUnrealized;
                cumulative += dayRealized + finalLeg;
            }

            totalAdjustments += dayAdjustments;
        }

        if (pnlSeries.Count == 0) return Fail("No simulation data (option history may be missing)", 404);

        var (okQ, respQ, _) = quote(underlyingQuoteSymbol, quoteExchange);
        var ltp = okQ && respQ["data"] is JsonObject d ? OptionChainReading.Num(d, "ltp") : 0;
        var values = pnlSeries.Select(p => OptionChainReading.Num(p, "pnl")).ToList();
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["underlying"] = baseSymbol, ["underlying_ltp"] = ltp, ["expiry_date"] = expiry, ["interval"] = interval, ["days_to_expiry"] = StraddleChartTool.DaysToExpiry(expiryDate, nowIst),
                ["adjustment_points"] = adjustmentPoints, ["lot_size"] = lotSize, ["lots"] = lots, ["quantity"] = quantity, ["pnl_series"] = StrategyChartSupport.ToArray(pnlSeries), ["trades"] = trades,
                ["summary"] = new JsonObject
                {
                    ["total_pnl"] = PyNumber.Round(cumulative, 2), ["total_adjustments"] = totalAdjustments, ["max_pnl"] = PyNumber.Round(values.Max(), 2), ["min_pnl"] = PyNumber.Round(values.Min(), 2),
                },
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
