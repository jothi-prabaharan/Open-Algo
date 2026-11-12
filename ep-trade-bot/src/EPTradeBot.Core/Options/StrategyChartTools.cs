using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// The Strategy Builder's combined-premium chart and its per-leg open-interest chart. OpenAlgo's get_strategy_chart_data and get_multi_strike_oi_data with history, quotes and the underlying
/// reference passed in.
/// </summary>
public static class StrategyChartTools
{
    public delegate (bool Ok, JsonObject Response, int Code) HistoryFetch(string symbol, string exchange, string interval, string start, string end);

    public delegate (string Symbol, string Exchange)? ReferenceResolver(string baseSymbol, string exchange, string? underlyingSymbol, string? underlyingExchange);

    public delegate (bool Ok, JsonObject Response, int Code) QuoteFetch(string symbol, string exchange);

    public sealed record Request(string Underlying, string Exchange, JsonArray? Legs, string Interval, int Days, string? UnderlyingSymbol, string? UnderlyingExchange, string? StartDate, string? EndDate);

    private static (bool Ok, JsonObject Response, int Code) Fail(string message, int code) => (false, new JsonObject { ["status"] = "error", ["message"] = message }, code);

    private static double LatestLtp(QuoteFetch quote, string symbol, string exchange)
    {
        var (ok, response, _) = quote(symbol, exchange);
        return ok && response["data"] is JsonObject d ? OptionChainReading.Num(d, "ltp") : 0;
    }

    /// <summary>The window, the underlying and the legs that count, or the refusal. Shared by both charts.</summary>
    private static ((string Start, string End) Window, bool Explicit, string Base, List<StrategyChartSupport.Leg> Legs, (string Symbol, string Exchange) Reference)? Prepare(
        Request r, ReferenceResolver resolve, DateTime nowIst, out (bool Ok, JsonObject Response, int Code) failure)
    {
        failure = default;
        (string, string)? explicitWindow;
        try
        {
            explicitWindow = StrategyChartSupport.ExplicitWindow(r.StartDate, r.EndDate, nowIst);
        }
        catch (StrategyChartSupport.WindowException e)
        {
            failure = Fail(e.Message, 400);
            return null;
        }

        var window = explicitWindow ?? TradingWindow.Resolve(r.Days, nowIst);
        var baseSymbol = (r.Underlying ?? "").Trim().ToUpperInvariant();
        if (baseSymbol.Length == 0) { failure = Fail("underlying is required", 400); return null; }
        var legs = (r.Legs ?? []).Select(StrategyChartSupport.NormalizeLeg).Where(l => l is not null).Select(l => l!).ToList();
        if (legs.Count == 0) { failure = Fail("No active option legs provided", 400); return null; }
        if (resolve(baseSymbol, r.Exchange, r.UnderlyingSymbol, r.UnderlyingExchange) is not { } reference)
        {
            failure = Fail($"No unexpired futures found for {baseSymbol} on {r.Exchange.ToUpperInvariant()}", 404);
            return null;
        }

        return (window, explicitWindow is not null, baseSymbol, legs, reference);
    }

    public static (bool Ok, JsonObject Response, int Code) CombinedPremium(Request r, ReferenceResolver resolve, HistoryFetch history, QuoteFetch quote, DateTime nowIst)
    {
        var prepared = Prepare(r, resolve, nowIst, out var failure);
        if (prepared is null) return failure;
        var (window, isExplicit, baseSymbol, legs, reference) = prepared.Value;

        var underlyingMissing = false;
        List<(long Time, JsonObject Row)>? underlying = null;
        var (okU, respU, _) = history(reference.Symbol, reference.Exchange, r.Interval, window.Start, window.End);
        if (!okU) underlyingMissing = true;
        else if ((respU["data"] as JsonArray ?? []) is { Count: 0 }) underlyingMissing = true;
        else
        {
            underlying = StrategyChartSupport.Candles((JsonArray)respU["data"]!);
            if (underlying is null) return Fail("Failed to parse underlying timestamps", 500);
        }

        var prices = new Dictionary<(string, string), Dictionary<long, double>>();
        foreach (var key in legs.Select(l => (l.Symbol, l.Exchange)).Distinct())
        {
            var (ok, resp, _) = history(key.Symbol, key.Exchange, r.Interval, window.Start, window.End);
            var map = new Dictionary<long, double>();
            if (ok && resp["data"] is JsonArray { Count: > 0 } data && StrategyChartSupport.Candles(data) is { } candles)
                foreach (var (time, row) in candles)
                    if (row["close"] is JsonValue v && TryDouble(v, out var close)) map[time] = close;
            prices[key] = map;
        }

        IEnumerable<(long Time, double? Spot)> timeline;
        if (underlying is not null)
            timeline = underlying.Where(c => c.Row["close"] is JsonValue v && TryDouble(v, out _)).Select(c => (c.Time, (double?)OptionChainReading.Num(c.Row, "close")));
        else
        {
            HashSet<long>? common = null;
            foreach (var map in prices.Values) { common ??= [.. map.Keys]; common.IntersectWith(map.Keys); }
            timeline = (common ?? []).OrderBy(t => t).Select(t => (t, (double?)null));
        }

        var series = new List<JsonObject>();
        foreach (var (time, spot) in timeline)
        {
            double signed = 0;
            var complete = true;
            foreach (var leg in legs)
            {
                if (!prices[(leg.Symbol, leg.Exchange)].TryGetValue(time, out var price)) { complete = false; break; }
                signed += leg.Sign * price;
            }

            if (!complete) continue;
            var point = new JsonObject { ["time"] = time, ["net_premium"] = PyNumber.Round(signed, 2), ["combined_premium"] = PyNumber.Round(Math.Abs(signed), 2) };
            if (spot is { } s) point["underlying"] = PyNumber.Round(s, 2);
            series.Add(point);
        }

        if (series.Count == 0) return Fail("No overlapping history across legs — option data may be unavailable for the selected range", 404);
        if (!isExplicit) series = StrategyChartSupport.CapLastTradingDates(series, r.Days);

        var entry = legs.Sum(l => l.Sign * l.EntryPrice);
        var tag = entry > 0 ? "credit" : entry < 0 ? "debit" : "flat";
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["underlying"] = baseSymbol, ["underlying_ltp"] = LatestLtp(quote, reference.Symbol, reference.Exchange), ["interval"] = r.Interval, ["tag"] = tag,
                ["entry_net_premium"] = PyNumber.Round(entry, 2), ["entry_abs_premium"] = PyNumber.Round(Math.Abs(entry), 2), ["legs_used"] = legs.Count,
                ["underlying_available"] = !underlyingMissing, ["series"] = StrategyChartSupport.ToArray(series),
            },
        }, 200);
    }

    public static (bool Ok, JsonObject Response, int Code) MultiStrikeOi(Request r, ReferenceResolver resolve, HistoryFetch history, QuoteFetch quote, DateTime nowIst)
    {
        var prepared = Prepare(r, resolve, nowIst, out var failure);
        if (prepared is null) return failure;
        var (window, isExplicit, baseSymbol, legs, reference) = prepared.Value;

        var underlyingMissing = false;
        var underlyingSeries = new List<JsonObject>();
        var (okU, respU, _) = history(reference.Symbol, reference.Exchange, r.Interval, window.Start, window.End);
        if (!okU || (respU["data"] as JsonArray ?? []) is { Count: 0 }) underlyingMissing = true;
        else
        {
            var candles = StrategyChartSupport.Candles((JsonArray)respU["data"]!);
            if (candles is null) return Fail("Failed to parse underlying timestamps", 500);
            foreach (var (time, row) in candles)
                if (row["close"] is JsonValue v && TryDouble(v, out var close)) underlyingSeries.Add(new JsonObject { ["time"] = time, ["value"] = PyNumber.Round(close, 2) });
        }

        var lookup = new Dictionary<(string, string), List<JsonObject>>();
        foreach (var key in legs.Select(l => (l.Symbol, l.Exchange)).Distinct())
        {
            var (ok, resp, _) = history(key.Symbol, key.Exchange, r.Interval, window.Start, window.End);
            var series = new List<JsonObject>();
            if (ok && resp["data"] is JsonArray { Count: > 0 } data && StrategyChartSupport.Candles(data) is { } candles)
                foreach (var (time, row) in candles)
                    series.Add(new JsonObject { ["time"] = time, ["value"] = PyNumber.Round(OiValue(row), 2) });
            lookup[key] = series;
        }

        var legSeries = new List<(JsonObject Entry, List<JsonObject> Series)>();
        foreach (var raw in r.Legs ?? [])
        {
            if (StrategyChartSupport.NormalizeLeg(raw) is not { } norm) continue;
            var series = lookup.GetValueOrDefault((norm.Symbol, norm.Exchange)) ?? [];
            var entry = new JsonObject
            {
                ["symbol"] = norm.Symbol, ["exchange"] = norm.Exchange, ["side"] = norm.Side, ["strike"] = raw!["strike"]?.DeepClone(), ["option_type"] = raw["optionType"]?.DeepClone(),
                ["expiry"] = raw["expiry"]?.DeepClone(), ["has_oi"] = series.Any(p => OptionChainReading.Num(p, "value") > 0),
            };
            legSeries.Add((entry, series));
        }

        if (!isExplicit)
        {
            underlyingSeries = StrategyChartSupport.CapLastTradingDates(underlyingSeries, r.Days);
            for (var i = 0; i < legSeries.Count; i++) legSeries[i] = (legSeries[i].Entry, StrategyChartSupport.CapLastTradingDates(legSeries[i].Series, r.Days));
        }

        var legsArray = new JsonArray();
        foreach (var (entry, series) in legSeries) { entry["series"] = StrategyChartSupport.ToArray(series); legsArray.Add(entry); }
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["underlying"] = baseSymbol, ["underlying_ltp"] = LatestLtp(quote, reference.Symbol, reference.Exchange), ["interval"] = r.Interval, ["underlying_available"] = !underlyingMissing,
                ["underlying_series"] = StrategyChartSupport.ToArray(underlyingSeries), ["legs"] = legsArray,
            },
        }, 200);
    }

    private static double OiValue(JsonObject row) => row["oi"] is JsonValue v && TryDouble(v, out var d) ? d : 0.0;

    private static bool TryDouble(JsonValue v, out double d)
    {
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return true;
        d = 0;
        return false;
    }
}
