using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// Futures candles beside the open-interest butterfly and the daily change in open interest per strike. OpenAlgo's get_oi_profile_data: the previous day's OI comes from daily history, fetched a few
/// contracts at a time with a pause between batches and a back-off on a refusal for going too fast.
/// </summary>
public static class OiProfileTool
{
    private const int BatchSize = 5;
    private const double BatchDelay = 0.5;
    private const int MaxRetries = 2;
    private const double RetryBaseDelay = 1.0;

    public delegate (bool Ok, JsonObject Response, int Code) HistoryFetch(string symbol, string exchange, string interval, string start, string end);

    private static (string Symbol, double? PreviousOi) FetchOne(string symbol, string exchange, string start, string end, HistoryFetch history, Action<double> sleep)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var (ok, response, code) = history(symbol, exchange, "D", start, end);
                if (ok && response["data"] is JsonArray { Count: > 0 } data)
                    return (symbol, data.Count >= 2 ? OptionChainReading.Num(data[^2], "oi") : 0.0);
                if (code == 429 && attempt < MaxRetries)
                {
                    sleep(RetryBaseDelay * Math.Pow(2, attempt));
                    continue;
                }

                return (symbol, 0.0);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (attempt < MaxRetries && e.Message.Contains("429", StringComparison.Ordinal))
                {
                    sleep(RetryBaseDelay * Math.Pow(2, attempt));
                    continue;
                }

                return (symbol, 0.0);
            }
        }

        return (symbol, 0.0);
    }

    /// <summary>Yesterday's open interest for each contract that has any today.</summary>
    public static Dictionary<string, double> FetchPreviousOi(IReadOnlyList<(string Symbol, double Oi)> contracts, string optionsExchange, HistoryFetch history, Action<double> sleep, DateTime nowIst)
    {
        var end = nowIst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = nowIst.AddDays(-14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var results = new Dictionary<string, double>();
        var symbols = contracts.Where(c => c.Oi > 0).Select(c => c.Symbol).ToList();
        for (var i = 0; i < symbols.Count; i += BatchSize)
        {
            foreach (var symbol in symbols.Skip(i).Take(BatchSize))
            {
                var (sym, previous) = FetchOne(symbol, optionsExchange, start, end, history, sleep);
                if (previous is not null) results[sym] = previous.Value;
            }

            if (i + BatchSize < symbols.Count) sleep(BatchDelay);
        }

        return results;
    }

    public static (bool Ok, JsonObject Response, int Code) Compute(
        JsonObject chainResponse, string underlying, string exchange, string expiryDate, string interval, int days,
        (string Symbol, string Exchange)? futures, HistoryFetch history, Action<double> sleep, DateTime nowIst)
    {
        try
        {
            var optionsExchange = OptionChainReading.OptionsExchange(exchange);
            var chain = chainResponse["chain"] as JsonArray ?? [];
            double? lot = null;
            var rows = new List<(JsonNode? Strike, double CeOi, double PeOi, string? CeSymbol, string? PeSymbol)>();
            var contracts = new List<(string Symbol, double Oi)>();
            foreach (var item in chain)
            {
                var ce = item?["ce"] is JsonObject c && c.Count > 0 ? c : null;
                var pe = item?["pe"] is JsonObject p && p.Count > 0 ? p : null;
                double ceOi = 0, peOi = 0;
                string? ceSymbol = null, peSymbol = null;
                if (ce is not null)
                {
                    ceOi = OptionChainReading.Num(ce, "oi");
                    ceSymbol = OptionChainReading.Text(ce, "symbol");
                    if (lot is null && OptionChainReading.Num(ce, "lotsize") != 0) lot = OptionChainReading.Num(ce, "lotsize");
                    if (!string.IsNullOrEmpty(ceSymbol) && ceOi > 0) contracts.Add((ceSymbol, ceOi));
                }

                if (pe is not null)
                {
                    peOi = OptionChainReading.Num(pe, "oi");
                    peSymbol = OptionChainReading.Text(pe, "symbol");
                    if (lot is null && OptionChainReading.Num(pe, "lotsize") != 0) lot = OptionChainReading.Num(pe, "lotsize");
                    if (!string.IsNullOrEmpty(peSymbol) && peOi > 0) contracts.Add((peSymbol, peOi));
                }

                rows.Add((item!["strike"], ceOi, peOi, ceSymbol, peSymbol));
            }

            var candles = new JsonArray();
            string? futuresSymbol = null;
            if (futures is { } f)
            {
                futuresSymbol = f.Symbol;
                var (start, end) = TradingWindow.Resolve(days, nowIst);
                var (okHistory, response, _) = history(f.Symbol, f.Exchange, interval, start, end);
                if (okHistory && response["data"] is JsonArray { Count: > 0 } data)
                {
                    var copy = (JsonArray)data.DeepClone();
                    TradingWindow.TagTime(copy);
                    candles = TradingWindow.CapLastTradingDates(copy, days);
                }
            }

            var previous = FetchPreviousOi(contracts, optionsExchange, history, sleep, nowIst);
            var requested = contracts.Select(c => c.Symbol).Distinct().Count();
            var loaded = previous.Count;

            var oiChain = new JsonArray();
            foreach (var r in rows)
            {
                double ceChange = 0, peChange = 0;
                if (!string.IsNullOrEmpty(r.CeSymbol) && previous.TryGetValue(r.CeSymbol, out var pc)) ceChange = r.CeOi - pc;
                if (!string.IsNullOrEmpty(r.PeSymbol) && previous.TryGetValue(r.PeSymbol, out var pp)) peChange = r.PeOi - pp;
                oiChain.Add(new JsonObject { ["strike"] = r.Strike?.DeepClone(), ["ce_oi"] = r.CeOi, ["pe_oi"] = r.PeOi, ["ce_oi_change"] = ceChange, ["pe_oi_change"] = peChange });
            }

            var response2 = new JsonObject
            {
                ["status"] = "success", ["underlying"] = chainResponse["underlying"]?.DeepClone() ?? underlying, ["spot_price"] = chainResponse["underlying_ltp"]?.DeepClone(),
                ["atm_strike"] = chainResponse["atm_strike"]?.DeepClone(), ["lot_size"] = lot is null or 0 ? 1 : lot, ["expiry_date"] = expiryDate, ["futures_symbol"] = futuresSymbol,
                ["interval"] = interval, ["candles"] = candles, ["oi_chain"] = oiChain,
            };
            if (loaded < requested)
            {
                response2["message"] = $"Loading the previous day's OI took too long, so the daily OI change is shown for {loaded} of {requested} option contracts. Refresh in a minute to load the rest.";
                response2["oi_change_loaded"] = loaded;
                response2["oi_change_requested"] = requested;
            }

            return (true, response2, 200);
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or FormatException or ArgumentException)
        {
            return (false, new JsonObject { ["status"] = "error", ["message"] = "Error fetching OI Profile data" }, 500);
        }
    }
}
