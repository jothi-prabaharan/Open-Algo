using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Portfolio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// /sip/backtest, /sip/frequencies, /sectorrotation and /sectorrotation/options. The maths is in Core; this is the price
/// loading OpenAlgo does through portfolio.data: daily closes from Historify (the candles table) or from the broker.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    public static V1Result SipFrequencies()
    {
        var labels = new Dictionary<string, string> { ["weekly"] = "Weekly", ["fortnightly"] = "Fortnightly", ["monthly"] = "Monthly", ["quarterly"] = "Quarterly" };
        var rows = SipSchedule.Frequencies.Select(f => (JsonNode)new JsonObject { ["value"] = f, ["label"] = labels.GetValueOrDefault(f, f) }).ToArray();
        return new V1Result(200, new JsonObject { ["status"] = "success", ["data"] = new JsonArray(rows) });
    }

    public static V1Result SectorRotationOptions()
    {
        var (_, response, code) = SectorRotationTool.ListOptions();
        return new V1Result(code, response);
    }

    public async Task<V1Result> SipBacktestAsync(BrokerAccount account, SipBacktestRequest r, CancellationToken ct)
    {
        var charges = r.Charges?.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double?>)kv.Value);
        var request = new SipRequest(r.Symbol, r.Exchange, JsonValue.Create(r.StartDate), JsonValue.Create(r.EndDate), JsonValue.Create(r.Amount), r.Frequency, JsonValue.Create((long)r.DayOfMonth),
            r.StepUpPercent, r.BrokeragePercent, r.BrokerageFlat, r.CostModel, r.CostExchange, charges, r.GstRate, r.CostBps, r.Slippage, r.Benchmark, r.BenchmarkExchange, r.Source, r.IncludeGrids);
        try
        {
            // The simulation and the sync price loader run on a pool thread; the loader waits on the database or the broker.
            var (_, response, code) = await Task.Run(() => SipService.Run(request, (symbol, exchange, start, end, source, _) =>
                LoadDailyCloses(account, symbol, exchange, start, end, source, ct)), ct);
            return new V1Result(code, response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "SIP backtest failed");
            return V1Result.Error(500, "SIP backtest failed.");
        }
    }

    public async Task<V1Result> SectorRotationAsync(BrokerAccount account, SectorRotationRequest r, CancellationToken ct)
    {
        var instruments = new JsonArray(r.Instruments.Select(i => (JsonNode)new JsonObject { ["symbol"] = i.Symbol, ["exchange"] = i.Exchange, ["label"] = i.Label }).ToArray());
        var today = DateOnly.FromDateTime(NowIst());
        try
        {
            var (_, response, code) = await Task.Run(() => SectorRotationTool.Compute(r.Benchmark, r.Timeframe, r.Source, instruments, today, (instrument, start, end, source) =>
                LoadDailyCloses(account, instrument.Symbol, instrument.Exchange, start, end, source, ct).Closes), ct);
            return new V1Result(code, response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sector rotation failed");
            return V1Result.Error(500, "The sector map could not be built. Details are in the error log.");
        }
    }

    /// <summary>Daily closes for one symbol from Historify ("db") or the broker ("api"), with the unadjusted-split warnings.</summary>
    private LoadedPrices LoadDailyCloses(BrokerAccount account, string symbol, string exchange, string start, string end, string source, CancellationToken ct, int minSessions = 2)
    {
        // The exchange was checked against the route's list when the request was validated.
        var ex = exchange.Trim().ToUpperInvariant();

        List<(DateOnly, double?)> bars;
        if (source == "db")
        {
            var from = DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = DateOnly.ParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Bars are stored at UTC; the session is the Indian calendar day, so read a day either side and cut on the Indian date.
            var lo = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1);
            var hi = to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(2);
            using var db = contexts.CreateDbContext();
            var rows = db.Candles.AsNoTracking().Where(c => c.Symbol == symbol && c.Exchange == ex && c.Interval == "D" && c.Ts >= lo && c.Ts < hi)
                .OrderBy(c => c.Ts).Select(c => new { c.Ts, c.Close }).ToList();
            bars = rows.Select(c => (DateOnly.FromDateTime(DateTime.SpecifyKind(c.Ts, DateTimeKind.Utc) + EPTradeBot.Core.Time.Ist.Offset), (double?)c.Close))
                .Where(b => b.Item1 >= from && b.Item1 <= to).ToList();
            if (bars.Count == 0)
                throw new PriceDataException($"Historify holds no D bars for {symbol} between {start} and {end}");
        }
        else
        {
            var answer = HistoryAsync(account, new HistoryRequest { Symbol = symbol, Exchange = ex, Interval = "D", StartDate = start, EndDate = end, Source = "api" }, ct).GetAwaiter().GetResult();
            if (answer.Status != 200)
                throw new PriceDataException($"{symbol} ({ex}): history request failed [{answer.Status}] {Text(answer.Body["message"])}".Trim());
            bars = [];
            foreach (var row in (answer.Body["data"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var stamp = row["timestamp"] ?? row["time"] ?? row["date"] ?? row["datetime"];
                if (stamp is null) continue;
                DateOnly? day = null;
                if (stamp is JsonValue v && v.TryGetValue<long>(out var epoch)) day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime);
                else if (stamp is JsonValue t && t.TryGetValue<string>(out var text) && text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", out var parsed)) day = parsed;
                if (day is { } d) bars.Add((d, BookFormatting.Number(row["close"])));
            }

            if (bars.Count == 0) throw new PriceDataException($"{symbol}: history response contained no bars");
        }

        var series = PriceSeriesBuilder.Build(bars, symbol, start, end, minSessions);
        return new LoadedPrices(series, source, PriceSeriesBuilder.SplitWarnings(series));
    }

    // ---- /portfolio -----------------------------------------------------------------------------------------

    private const string NoApiSession = "No broker session for source='api'. Log in to your broker, or use source='db' to backtest from local history.";

    public async Task<V1Result> PortfolioBenchmarksAsync(BrokerAccount account, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Set<SymToken>().AsNoTracking().Where(s => s.Broker == account.Broker && (s.Exchange == "NSE_INDEX" || s.Exchange == "BSE_INDEX" || s.Exchange == "GLOBAL_INDEX"))
            .OrderBy(s => s.Exchange).ThenBy(s => s.Symbol).Select(s => new { s.Symbol, s.Exchange, s.Name }).ToListAsync(ct);
        return new V1Result(200, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["name"] = r.Name }).ToArray()),
        });
    }

    private PortfolioBacktestService.MatrixLoader MatrixLoader(BrokerAccount account, CancellationToken ct) => (symbols, exchanges, start, end, source, _) =>
    {
        var columns = new List<(string, PriceSeries)>();
        var missing = new List<(string Symbol, PriceDataException Error)>();
        for (var i = 0; i < symbols.Count; i++)
        {
            try
            {
                columns.Add((symbols[i], LoadDailyCloses(account, symbols[i], exchanges[i], start, end, source, ct, minSessions: 1).Closes));
            }
            catch (PriceDataException e)
            {
                missing.Add((symbols[i], e));
                if (source == "api") break;
            }
        }

        if (missing.Count > 0)
        {
            // Historify names every symbol when it holds bars for none of them; otherwise it names the first one it lacks.
            if (source == "db" && columns.Count == 0)
                throw new PriceDataException($"Historify holds no D bars for {string.Join(", ", symbols.Distinct().Order(StringComparer.Ordinal))} between {start} and {end}");
            if (source == "db") throw new PriceDataException($"{missing[0].Symbol}: no D history in Historify for {start}..{end}. Ingest it, or run with the broker API as the source.");
            throw missing[0].Error;
        }

        return PriceMatrix.Join(columns, source, start, end);
    };

    private Dictionary<string, string> ListingNames(BrokerAccount account, IReadOnlyCollection<string> symbols)
    {
        using var db = contexts.CreateDbContext();
        var rows = db.Set<SymToken>().AsNoTracking().Where(s => s.Broker == account.Broker && symbols.Contains(s.Symbol)).Select(s => new { s.Symbol, s.Name }).ToList();
        var names = new Dictionary<string, string>();
        foreach (var r in rows) names[r.Symbol] = r.Name ?? string.Empty;
        return names;
    }

    private static PortfolioBacktestInput BacktestInput(PortfolioBacktestRequest r) => new(
        [.. r.Holdings.Select(h => new PortfolioHolding(h.Symbol, h.Exchange, h.Weight ?? 0))], r.StartDate, r.EndDate, r.Benchmark, r.BenchmarkExchange, r.Rebalance, r.DriftBand, r.CostModel, r.BrokeragePct, r.CostExchange,
        r.Charges.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double?>)kv.Value), r.GstRate, 1.0, 0.5, 1000, r.CostBps, r.Slippage, r.InitialCapital, r.RiskFreeRate, r.Source);

    public async Task<V1Result> PortfolioBacktestAsync(BrokerAccount account, PortfolioBacktestRequest r, CancellationToken ct)
    {
        if (r.Source == "api" && !Broker<IBrokerHistory>(account, out _, out _, out _)) return V1Result.Error(403, NoApiSession);
        try
        {
            var names = ListingNames(account, [.. r.Holdings.Select(h => h.Symbol.Trim().ToUpperInvariant())]);
            var loader = MatrixLoader(account, ct);
            var (_, response, code) = await Task.Run(() => PortfolioBacktestService.Run(BacktestInput(r), loader, names), ct);
            return new V1Result(code, response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Portfolio backtest failed");
            return V1Result.Error(500, "Backtest failed.");
        }
    }

    /// <summary>OpenAlgo renders openstatz's HTML tearsheet with matplotlib; EP Trade bot has no equivalent renderer yet, and says so.</summary>
    public V1Result PortfolioTearsheet(BrokerAccount account, PortfolioBacktestRequest r) =>
        r.Source == "api" && !Broker<IBrokerHistory>(account, out _, out _, out _)
            ? V1Result.Error(403, "No broker session for source='api'.")
            : V1Result.Error(501, "The downloadable tearsheet is not available in EP Trade bot yet. The Backtest tab shows the same figures.");

    public async Task<V1Result> PortfolioHoldingsAsync(BrokerAccount account, PortfolioHoldingsRequest r, CancellationToken ct)
    {
        if (!Broker<IBrokerPortfolio>(account, out _, out _, out _))
            return V1Result.Error(403, "No broker session. Log in to your broker to analyse live holdings.");
        try
        {
            var held = await HoldingsAsync(account, ct);
            if (held.Status != 200) return V1Result.Error(held.Status, Text(held.Body["message"]) ?? "could not fetch holdings");
            var rows = (held.Body["data"]?["holdings"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var positions = PortfolioHoldings.Parse(rows);
            if (positions.Count == 0) return V1Result.Error(422, "no holdings with a usable quantity and price");

            var summary = PortfolioHoldings.Summarise(positions);
            var summaryRows = (summary["holdings"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var tradable = summaryRows.Where(h => h["exchange"]!.GetValue<string>() is "NSE" or "BSE")
                .Select(h => new PortfolioHolding(h["symbol"]!.GetValue<string>(), h["exchange"]!.GetValue<string>(), h["weight"]!.GetValue<double>() * 100)).ToList();
            var skipped = summaryRows.Where(h => h["exchange"]!.GetValue<string>() is not ("NSE" or "BSE")).Select(h => (JsonNode)h["symbol"]!.GetValue<string>()).ToArray();

            var end = DateOnly.FromDateTime(NowIst());
            var start = end.AddDays(-r.LookbackDays);
            JsonNode? analysis = null;
            string? analysisError = null;
            if (tradable.Count > 0)
            {
                var names = ListingNames(account, [.. tradable.Select(t => t.Symbol)]);
                var input = new PortfolioBacktestInput(tradable, start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Benchmark, r.BenchmarkExchange,
                    RiskFreeRate: r.RiskFreeRate, Source: r.Source);
                var loader = MatrixLoader(account, ct);
                var (ok, result, _) = await Task.Run(() => PortfolioBacktestService.Run(input, loader, names), ct);
                if (ok) analysis = result;
                else analysisError = Text(result["message"]) ?? "analysis unavailable";
            }

            return new V1Result(200, new JsonObject
            {
                ["status"] = "success", ["summary"] = PortfolioBacktestService.CleanTree(summary), ["analysis"] = analysis, ["analysis_error"] = analysisError, ["skipped"] = new JsonArray(skipped),
                ["meta"] = new JsonObject
                {
                    ["lookback_days"] = r.LookbackDays, ["start"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["end"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["basis"] = "Current holdings weighted by market value, run over past prices. This shows how today's portfolio would have behaved, not how the account performed -- a holdings payload does not say when each lot was bought.",
                },
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Holdings analysis failed");
            return V1Result.Error(500, "Analysis failed.");
        }
    }
}
