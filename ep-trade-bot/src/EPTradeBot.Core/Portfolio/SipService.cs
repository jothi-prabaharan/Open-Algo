using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

/// <summary>The price history for a symbol could not be used (no history, too little, bad symbol); the message says why.</summary>
public sealed class PriceDataException(string message) : Exception(message);

/// <summary>Prices loaded for one symbol: the closes, where they came from, and any warnings about suspected unadjusted corporate actions.</summary>
public sealed record LoadedPrices(PriceSeries Closes, string Source, IReadOnlyList<string> Warnings);

public sealed record SipRequest(
    string Symbol, string? Exchange, JsonNode? StartDate, JsonNode? EndDate, JsonNode? Amount, string Frequency = "monthly", JsonNode? DayOfMonth = null, double StepUpPercent = 0,
    double BrokeragePercent = 0, double BrokerageFlat = 0, string CostModel = "indian_equity", string CostExchange = "NSE",
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double?>>? ChargeOverrides = null, double? GstRate = null, double CostBps = 0, double Slippage = 0,
    string? Benchmark = null, string BenchmarkExchange = "NSE_INDEX", string Source = "db", bool IncludeGrids = true);

/// <summary>
/// The SIP back tester: validation, price loading, cost model, the simulation and every metric the page shows. OpenAlgo's sip_service.run_sip_backtest with price loading passed in.
/// </summary>
public static class SipService
{
    public const int MaxSipYears = 30;
    public const string InvalidAmount = "amount must be a positive number";
    private static readonly string[] SupportedExchanges = ["NSE", "BSE"];
    private static readonly string[] BenchmarkExchanges = ["NSE_INDEX", "BSE_INDEX", "GLOBAL_INDEX"];

    public delegate LoadedPrices PriceLoader(string symbol, string exchange, string start, string end, string source, bool benchmark);

    private static (bool Ok, JsonObject Response, int Code) Error(string message, int status = 400) => (false, new JsonObject { ["status"] = "error", ["message"] = message }, status);

    /// <summary>Python's repr for the few values a date field can hold.</summary>
    private static string Repr(JsonNode? n)
    {
        if (n is null) return "None";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
                return s.Contains('\'') && !s.Contains('"') ? $"\"{s}\"" : $"'{s.Replace("\\", "\\\\").Replace("'", "\\'")}'";
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            return n.ToJsonString();
        }

        return n.ToJsonString();
    }

    private static string PyStr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n is null ? "None" : n is JsonValue v2 && v2.TryGetValue<bool>(out var b) ? (b ? "True" : "False") : n.ToJsonString();

    private static DateOnly ParseDate(JsonNode? value, string field)
    {
        var text = PyStr(value);
        text = text.Length > 10 ? text[..10] : text;
        if (DateTime.TryParseExact(text, ["yyyy-M-d", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return DateOnly.FromDateTime(d);
        throw new ArgumentException($"{field} must be YYYY-MM-DD, got {Repr(value)}");
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ITradeCosts BuildCosts(SipRequest r)
    {
        if (r.CostModel == "flat_bps") return new FlatCosts(r.CostBps, r.Slippage).Validate();
        var overrides = (r.ChargeOverrides ?? new Dictionary<string, IReadOnlyDictionary<string, double?>>()).ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double?>)new Dictionary<string, double?>(kv.Value));
        if ((r.BrokeragePercent != 0 || r.BrokerageFlat != 0) && !overrides.ContainsKey("brokerage"))
            overrides["brokerage"] = new Dictionary<string, double?> { ["flat"] = r.BrokerageFlat, ["rate"] = r.BrokeragePercent / 100.0, ["cap"] = 20.0 };
        var schedule = CostSchedule.For($"india_delivery_{r.CostExchange.ToLowerInvariant()}", overrides);
        return (schedule with { Slippage = r.Slippage, TaxRate = r.GstRate ?? schedule.TaxRate }).Validate();
    }

    private static bool TryAmount(JsonNode? node, out double amount)
    {
        amount = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out amount)) return true;
        if (v.TryGetValue<long>(out var l)) { amount = l; return true; }
        if (v.TryGetValue<bool>(out var b)) { amount = b ? 1 : 0; return true; }
        return v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount);
    }

    public static (bool Ok, JsonObject Response, int Code) Run(SipRequest r, PriceLoader load)
    {
        DateOnly start, end;
        try
        {
            start = ParseDate(r.StartDate, "start_date");
            end = ParseDate(r.EndDate, "end_date");
        }
        catch (ArgumentException e)
        {
            return Error(e.Message);
        }

        if (end <= start) return Error("end_date must be after start_date");
        if (end.DayNumber - start.DayNumber > MaxSipYears * 366) return Error($"SIP window cannot exceed {MaxSipYears} years");
        if (!TryAmount(r.Amount, out var amount) || !double.IsFinite(amount) || amount <= 0) return Error(InvalidAmount);
        if (!SipSchedule.Frequencies.Contains(r.Frequency)) return Error($"frequency must be one of {string.Join(", ", SipSchedule.Frequencies)}");
        // day_of_month only matters for monthly and quarterly SIPs, and then it must be a whole number from 1 to 28 (a boolean or a decimal is refused).
        var dayOfMonth = 1;
        long? dayWhole = r.DayOfMonth is JsonValue dv && !dv.TryGetValue<bool>(out _) && dv.TryGetValue<long>(out var dl) ? dl : null;
        if (r.Frequency is "monthly" or "quarterly" && r.DayOfMonth is not null && (dayWhole is null || dayWhole < 1 || dayWhole > SipSchedule.MaxDayOfMonth))
            return Error($"day_of_month must be an integer between 1 and {SipSchedule.MaxDayOfMonth}");
        if (dayWhole is { } whole) dayOfMonth = (int)Math.Clamp(whole, int.MinValue, int.MaxValue);

        var exchange = string.IsNullOrEmpty(r.Exchange) ? "NSE" : r.Exchange.ToUpperInvariant();
        if (!SupportedExchanges.Contains(exchange)) return Error($"exchange must be one of {string.Join(", ", SupportedExchanges)} for a SIP");
        if (!string.IsNullOrEmpty(r.Benchmark) && !BenchmarkExchanges.Contains(r.BenchmarkExchange.ToUpperInvariant()))
            return Error($"benchmark_exchange must be one of {string.Join(", ", BenchmarkExchanges)}");

        LoadedPrices matrix;
        try
        {
            matrix = load(r.Symbol, exchange, Iso(start), Iso(end), r.Source, false);
        }
        catch (PriceDataException e)
        {
            return Error(e.Message, 422);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Error($"could not load prices for {r.Symbol}: {e.Message}", 502);
        }

        var closes = matrix.Closes;
        ITradeCosts costs;
        try
        {
            costs = BuildCosts(r);
        }
        catch (ArgumentException e)
        {
            return Error(e.Message);
        }

        var options = new SipOptions(r.Frequency, dayOfMonth, r.StepUpPercent, costs);
        SipResult result;
        try
        {
            result = SipEngine.Run(closes, r.Symbol, exchange, start, end, amount, options, matrix.Warnings);
        }
        catch (Exception e) when (e is SipException or ScheduleException)
        {
            return Error(e.Message, 422);
        }

        var payload = new JsonObject
        {
            ["status"] = "success",
            ["request"] = new JsonObject
            {
                ["symbol"] = r.Symbol, ["exchange"] = exchange, ["start_date"] = Iso(start), ["end_date"] = Iso(end), ["amount"] = amount, ["frequency"] = r.Frequency,
                ["day_of_month"] = r.DayOfMonth?.DeepClone() ?? 1, ["step_up_percent"] = r.StepUpPercent, ["source"] = matrix.Source,
            },
            ["headline"] = SipAnalytics.Headline(result),
            ["underwater"] = SipAnalytics.Underwater(result),
            ["drawdown"] = SipAnalytics.Drawdown(result),
            ["yearly"] = SipAnalytics.YearlyBreakdown(result),
            ["monthly_heatmap"] = SipAnalytics.MonthlyValueHeatmap(result),
            ["lumpsum"] = SipAnalytics.LumpsumComparison(closes, result, start, end, options),
            ["charges"] = new JsonObject
            {
                ["total"] = PyNumber.Round(result.Charges, 2), ["per_installment"] = PyNumber.Round(result.Charges / Math.Max(result.InstallmentCount, 1), 2),
                ["drag"] = result.TotalInvested != 0 ? PyNumber.Round(result.Charges / result.TotalInvested, 6) : null,
                ["breakdown"] = new JsonObject(result.ChargeBreakdown.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))), ["model"] = r.CostModel, ["exchange"] = r.CostExchange,
            },
            ["frequency_comparison"] = SipAnalytics.FrequencyComparison(closes, start, end, amount, options with { Frequency = r.Frequency }),
            ["curves"] = new JsonObject { ["value"] = Curve(result.Dates, result.Value), ["invested"] = Curve(result.Dates, result.Invested) },
            ["installments"] = new JsonArray(result.Installments.Select(i => (JsonNode)new JsonObject { ["requested"] = Iso(i.Requested), ["executed"] = Iso(i.Executed), ["amount"] = PyNumber.Round(i.Amount, 2) }).ToArray()),
            ["warnings"] = new JsonArray(result.Warnings.Select(w => (JsonNode)w).ToArray()),
        };

        if (r.IncludeGrids)
        {
            payload["rolling_xirr"] = SipAnalytics.RollingXirr(closes, amount, options);
            payload["start_date_heatmap"] = SipAnalytics.StartDateHeatmap(closes, amount, options);
            var crisis = SipCrisis.Analyse(closes, amount, options);
            payload["crisis"] = new JsonObject { ["summary"] = SipCrisis.Summary(crisis), ["periods"] = new JsonArray(crisis.Select(c => (JsonNode)c).ToArray()) };
            payload["sip_date_heatmap"] = r.Frequency is "monthly" or "quarterly" ? SipAnalytics.SipDateHeatmap(closes, start, end, amount, options) : null;
        }

        if (!string.IsNullOrEmpty(r.Benchmark)) payload["benchmark"] = BenchmarkSip(r.Benchmark, r.BenchmarkExchange.ToUpperInvariant(), start, end, amount, result, r.Source, load, options);
        return (true, payload, 200);
    }

    private static JsonArray Curve(DateOnly[] dates, double[] values)
    {
        var a = new JsonArray();
        for (var i = 0; i < dates.Length; i++) a.Add(new JsonObject { ["date"] = Iso(dates[i]), ["value"] = PyNumber.Round(values[i], 2) });
        return a;
    }

    private static JsonObject BenchmarkSip(string symbol, string exchange, DateOnly start, DateOnly end, double amount, SipResult result, string source, PriceLoader load, SipOptions options)
    {
        SipResult bench;
        try
        {
            var matrix = load(symbol, exchange, Iso(start), Iso(end), source, true);
            bench = SipEngine.Run(matrix.Closes, symbol, exchange, start, end, amount, options);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["error"] = e.Message };
        }

        var benchX = Xirr.OrNull(bench.CashFlows);
        var selfX = Xirr.OrNull(result.CashFlows);
        return new JsonObject
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["headline"] = SipAnalytics.Headline(bench), ["xirr"] = benchX,
            ["excess_xirr"] = selfX is { } a && benchX is { } b ? a - b : null, ["beat_benchmark"] = selfX is { } c && benchX is { } d && c > d, ["curve"] = Curve(bench.Dates, bench.Value),
        };
    }
}
