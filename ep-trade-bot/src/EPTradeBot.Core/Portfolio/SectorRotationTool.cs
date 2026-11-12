using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

public sealed record Instrument(string Symbol, string Exchange, string Label);

/// <summary>
/// The relative rotation map: every sector index placed against a benchmark by RS-Ratio (is it stronger than the benchmark?) and RS-Momentum (is that strength still building?), both centred on 100.
/// OpenAlgo's sector_rotation_service with price loading passed in.
/// </summary>
public static class SectorRotationTool
{
    public static readonly IReadOnlyList<Instrument> Sectors =
    [
        new("NIFTYAUTO", "NSE_INDEX", "Auto"), new("NIFTYCOMMODITIES", "NSE_INDEX", "Commodities"), new("NIFTYENERGY", "NSE_INDEX", "Energy"), new("FINNIFTY", "NSE_INDEX", "Financial Services"),
        new("NIFTYFMCG", "NSE_INDEX", "FMCG"), new("NIFTYINFRA", "NSE_INDEX", "Infra"), new("NIFTYIT", "NSE_INDEX", "IT"), new("NIFTYMEDIA", "NSE_INDEX", "Media"), new("NIFTYMETAL", "NSE_INDEX", "Metal"),
        new("NIFTYPHARMA", "NSE_INDEX", "Pharma"), new("NIFTYPVTBANK", "NSE_INDEX", "Private Bank"), new("NIFTYPSE", "NSE_INDEX", "PSE"), new("NIFTYPSUBANK", "NSE_INDEX", "PSU Bank"),
        new("NIFTYREALTY", "NSE_INDEX", "Realty"),
    ];

    public static readonly IReadOnlyList<Instrument> OptionalSectors =
    [
        new("NIFTYCONSRDURBL", "NSE_INDEX", "Consumer Durables"), new("NIFTYHEALTHCARE", "NSE_INDEX", "Healthcare"), new("NIFTYINDDEFENCE", "NSE_INDEX", "India Defence"), new("NIFTYOILANDGAS", "NSE_INDEX", "Oil & Gas"),
    ];

    public static readonly IReadOnlyList<Instrument> Benchmarks =
    [
        new("NIFTY", "NSE_INDEX", "Nifty 50"), new("NIFTY500", "NSE_INDEX", "Nifty 500"), new("BANKNIFTY", "NSE_INDEX", "Bank Nifty"), new("SENSEX", "BSE_INDEX", "Sensex"),
        new("NIFTYNXT50", "NSE_INDEX", "Nifty Next 50"), new("MIDCPNIFTY", "NSE_INDEX", "Midcap Nifty"), new("FINNIFTY", "NSE_INDEX", "Fin Nifty"),
    ];

    public static readonly string[] Timeframes = ["daily", "weekly"];
    public static readonly string[] Sources = ["db", "api"];
    public static readonly string[] Exchanges = ["NSE", "BSE", "NSE_INDEX", "BSE_INDEX", "MCX_INDEX", "GLOBAL_INDEX"];
    public const int MaxInstruments = 40;
    public const int MaxTail = 30;
    private static readonly Dictionary<string, (int Fast, int Slow, int Momentum)> Params = new() { ["daily"] = (14, 36, 8), ["weekly"] = (5, 15, 5) };
    private static readonly Dictionary<string, int> LookbackDays = new() { ["daily"] = 365, ["weekly"] = 730 };

    /// <summary>Daily closes for one instrument; throws <see cref="PriceDataException"/> when the source has none.</summary>
    public delegate PriceSeries CloseLoader(Instrument instrument, string start, string end, string source);

    private static (bool Ok, JsonObject Response, int Code) Error(string message, int status = 400) => (false, new JsonObject { ["status"] = "error", ["message"] = message }, status);

    private static JsonObject Describe(Instrument i) => new() { ["symbol"] = i.Symbol, ["exchange"] = i.Exchange, ["label"] = i.Label };

    public static (bool Ok, JsonObject Response, int Code) ListOptions() => (true, new JsonObject
    {
        ["status"] = "success",
        ["data"] = new JsonObject
        {
            ["benchmarks"] = new JsonArray(Benchmarks.Select(b => (JsonNode)Describe(b)).ToArray()), ["timeframes"] = new JsonArray(Timeframes.Select(t => (JsonNode)t).ToArray()),
            ["exchanges"] = new JsonArray(Exchanges.Select(e => (JsonNode)e).ToArray()), ["max_instruments"] = MaxInstruments, ["sectors"] = new JsonArray(Sectors.Select(s => (JsonNode)Describe(s)).ToArray()),
            ["optional_sectors"] = new JsonArray(OptionalSectors.Select(s => (JsonNode)Describe(s)).ToArray()), ["max_tail"] = MaxTail,
        },
    }, 200);

    /// <summary>One close per week ending Friday, each stamped with the last session that traded in it (not the Friday itself).</summary>
    public static PriceSeries ToWeekly(PriceSeries closes)
    {
        if (closes.IsEmpty) return closes;
        static DateOnly WeekEnd(DateOnly d) => d.AddDays((4 - (int)(((int)d.DayOfWeek + 6) % 7) + 7) % 7);
        var dates = new List<DateOnly>();
        var values = new List<double>();
        var currentEnd = default(DateOnly);
        var open = false;
        for (var i = 0; i < closes.Count; i++)
        {
            var end = WeekEnd(closes.Dates[i]);
            if (open && end == currentEnd) { dates[^1] = closes.Dates[i]; if (!double.IsNaN(closes.Closes[i])) values[^1] = closes.Closes[i]; }
            else { dates.Add(closes.Dates[i]); values.Add(closes.Closes[i]); currentEnd = end; open = true; }
        }

        return new PriceSeries(dates.ToArray(), values.ToArray());
    }

    /// <summary>The two series on the sessions both traded, resampled to weeks after alignment so a holiday for only one side drops nothing.</summary>
    public static (PriceSeries Symbol, PriceSeries Benchmark) PairedBars(PriceSeries closes, PriceSeries benchmark, string timeframe)
    {
        var bench = new Dictionary<DateOnly, double>();
        for (var i = 0; i < benchmark.Count; i++) bench[benchmark.Dates[i]] = benchmark.Closes[i];
        var dates = new List<DateOnly>();
        var a = new List<double>();
        var b = new List<double>();
        var seen = new HashSet<DateOnly>();
        foreach (var (d, v) in closes.Dates.Zip(closes.Closes).OrderBy(x => x.First))
            if (bench.TryGetValue(d, out var bv) && !double.IsNaN(v) && !double.IsNaN(bv) && seen.Add(d)) { dates.Add(d); a.Add(v); b.Add(bv); }
        var sym = new PriceSeries(dates.ToArray(), a.ToArray());
        var ben = new PriceSeries(dates.ToArray(), b.ToArray());
        return timeframe == "weekly" ? (ToWeekly(sym), ToWeekly(ben)) : (sym, ben);
    }

    /// <summary>Linearly weighted moving average, weights 1..length with the newest bar heaviest; NaN until a full window exists.</summary>
    public static double[] Wma(double[] values, int length)
    {
        var result = new double[values.Length];
        var total = length * (length + 1) / 2.0;
        for (var i = 0; i < values.Length; i++)
        {
            if (i + 1 < length) { result[i] = double.NaN; continue; }
            double sum = 0;
            for (var k = 0; k < length; k++) sum += values[i - length + 1 + k] * (k + 1);
            result[i] = sum / total;
        }

        return result;
    }

    public sealed record Point(DateOnly Date, double RsRatio, double RsMomentum);

    public static List<Point> RotationSeries(PriceSeries sector, PriceSeries benchmark, string timeframe)
    {
        var (fast, slow, momentum) = Params[timeframe];
        var bench = new Dictionary<DateOnly, double>();
        for (var i = 0; i < benchmark.Count; i++) bench[benchmark.Dates[i]] = benchmark.Closes[i];
        var rows = sector.Dates.Zip(sector.Closes).Where(r => bench.TryGetValue(r.First, out var b) && !double.IsNaN(r.Second) && !double.IsNaN(b)).OrderBy(r => r.First).ToList();
        if (rows.Count == 0) return [];
        var rs = rows.Select(r => 100 * r.Second / bench[r.First]).ToArray();
        var wf = Wma(rs, fast);
        var ws = Wma(rs, slow);
        var ratio = new double[rs.Length];
        for (var i = 0; i < rs.Length; i++) ratio[i] = 100 * wf[i] / ws[i];
        var points = new List<Point>();
        for (var i = 0; i < rs.Length; i++)
        {
            var m = i >= momentum ? 100 * ratio[i] / ratio[i - momentum] : double.NaN;
            if (!double.IsNaN(ratio[i]) && !double.IsNaN(m)) points.Add(new Point(rows[i].First, ratio[i], m));
        }

        return points;
    }

    public static string Quadrant(double ratio, double momentum) => ratio >= 100 ? (momentum >= 100 ? "leading" : "weakening") : (momentum >= 100 ? "improving" : "lagging");

    public static int BarsInQuadrant(IReadOnlyList<Point> points)
    {
        if (points.Count == 0) return 0;
        var current = Quadrant(points[^1].RsRatio, points[^1].RsMomentum);
        var count = 0;
        for (var i = points.Count - 1; i >= 0; i--)
        {
            if (Quadrant(points[i].RsRatio, points[i].RsMomentum) != current) break;
            count++;
        }

        return count;
    }

    public static JsonObject? Summarise(IReadOnlyList<Point> points, int tail = MaxTail)
    {
        if (points.Count == 0) return null;
        var last = points[^1];
        var trail = new JsonArray();
        foreach (var p in points.Skip(Math.Max(0, points.Count - tail)))
            trail.Add(new JsonObject { ["date"] = p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["rs_ratio"] = PyNumber.Round(p.RsRatio, 2), ["rs_momentum"] = PyNumber.Round(p.RsMomentum, 2) });
        return new JsonObject
        {
            ["quadrant"] = Quadrant(last.RsRatio, last.RsMomentum), ["rs_ratio"] = PyNumber.Round(last.RsRatio, 2), ["rs_momentum"] = PyNumber.Round(last.RsMomentum, 2),
            ["bars_in_quadrant"] = BarsInQuadrant(points), ["tail"] = trail,
        };
    }

    private static string AsText(JsonNode? n) => n is null ? string.Empty : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();

    public static (List<Instrument> Members, string? Problem) ResolveInstruments(JsonArray? requested)
    {
        if (requested is null || requested.Count == 0) return ([.. Sectors], null);
        if (requested.Count > MaxInstruments) return ([], $"The map holds at most {MaxInstruments} symbols.");
        var known = Sectors.Concat(OptionalSectors).ToDictionary(s => (s.Symbol, s.Exchange));
        var result = new List<Instrument>();
        var seen = new HashSet<(string, string)>();
        foreach (var item in requested)
        {
            var symbol = AsText((item as JsonObject)?["symbol"]).Trim().ToUpperInvariant();
            var exchange = AsText((item as JsonObject)?["exchange"]).Trim().ToUpperInvariant();
            if (symbol.Length == 0) return ([], "Every symbol on the map needs a name.");
            if (!Exchanges.Contains(exchange)) return ([], $"{symbol} is on {(exchange.Length > 0 ? exchange : "no exchange")}. The map takes symbols from {string.Join(", ", Exchanges)}.");
            if (!seen.Add((symbol, exchange))) continue;
            var label = AsText((item as JsonObject)?["label"]).Trim();
            if (label.Length == 0) label = known.TryGetValue((symbol, exchange), out var k) ? k.Label : symbol;
            result.Add(new Instrument(symbol, exchange, label));
        }

        return (result, null);
    }

    private static string MissingReason(string source) => source == "db"
        ? "Historify has no daily history for this symbol. Download it in Historify, or switch the data source to Broker API."
        : "Your broker returned no history for this symbol. Some brokers do not publish every sector index; Historify may have it.";

    public static (bool Ok, JsonObject Response, int Code) Compute(string benchmark, string timeframe, string source, JsonArray? instruments, DateOnly end, CloseLoader load)
    {
        var bench = Benchmarks.FirstOrDefault(b => b.Symbol == (benchmark ?? string.Empty).ToUpperInvariant());
        if (bench is null) return Error($"benchmark must be one of {string.Join(", ", Benchmarks.Select(b => b.Symbol))}");
        if (!Timeframes.Contains(timeframe)) return Error($"timeframe must be one of {string.Join(", ", Timeframes)}");
        if (!Sources.Contains(source)) return Error("source must be 'db' (Historify) or 'api' (broker)");
        var (members, problem) = ResolveInstruments(instruments);
        if (problem is not null) return Error(problem);

        var startText = end.AddDays(-LookbackDays[timeframe]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endText = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        PriceSeries benchCloses;
        try
        {
            benchCloses = load(bench, startText, endText, source);
        }
        catch (PriceDataException)
        {
            return Error(source == "db"
                ? $"Historify has no daily history for {bench.Label}. Download it in Historify, or switch the data source to Broker API."
                : $"Your broker returned no history for {bench.Label}. Pick another benchmark, or switch the data source to Historify.", 422);
        }

        var placed = new JsonArray();
        var unavailable = new List<JsonObject>();
        string? asOf = null;
        foreach (var member in members)
        {
            JsonObject Unavailable(string reason) => new() { ["symbol"] = member.Symbol, ["exchange"] = member.Exchange, ["label"] = member.Label, ["reason"] = reason };
            if (member.Symbol == bench.Symbol && member.Exchange == bench.Exchange)
            {
                unavailable.Add(Unavailable("This is the benchmark itself, so it would always sit at 100. Pick another benchmark to see it on the map."));
                continue;
            }

            PriceSeries closes;
            try
            {
                closes = load(member, startText, endText, source);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                unavailable.Add(Unavailable(MissingReason(source)));
                continue;
            }

            var (s, b) = PairedBars(closes, benchCloses, timeframe);
            var summary = Summarise(RotationSeries(s, b, timeframe));
            if (summary is null)
            {
                var unit = timeframe == "weekly" ? "weeks" : "sessions";
                var p = Params[timeframe];
                unavailable.Add(Unavailable($"Not enough {timeframe} history yet. The map needs at least {p.Slow + p.Momentum} {unit} that this symbol and the benchmark both traded."));
                continue;
            }

            var tailRows = (JsonArray)summary["tail"]!;
            var lastDate = tailRows[tailRows.Count - 1]!["date"]!.GetValue<string>();
            if (asOf is null || string.CompareOrdinal(lastDate, asOf) > 0) asOf = lastDate;
            var entry = new JsonObject { ["symbol"] = member.Symbol, ["exchange"] = member.Exchange, ["label"] = member.Label };
            foreach (var (k, v) in summary) entry[k] = v?.DeepClone();
            placed.Add(entry);
        }

        if (placed.Count == 0 && unavailable.Count > 0) return Error("Nothing could be placed on the map. " + unavailable[0]["reason"]!.GetValue<string>(), 422);
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["benchmark"] = bench.Symbol, ["benchmark_exchange"] = bench.Exchange, ["benchmark_label"] = bench.Label, ["timeframe"] = timeframe, ["source"] = source, ["as_of"] = asOf, ["max_tail"] = MaxTail,
                ["instruments"] = placed, ["unavailable"] = new JsonArray(unavailable.Select(u => (JsonNode)u).ToArray()),
            },
        }, 200);
    }
}
