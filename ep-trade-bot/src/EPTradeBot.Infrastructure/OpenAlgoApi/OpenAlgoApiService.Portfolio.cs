using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
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
    private LoadedPrices LoadDailyCloses(BrokerAccount account, string symbol, string exchange, string start, string end, string source, CancellationToken ct)
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
                throw new PriceDataException($"{symbol}: no D history in Historify for {start}..{end}. Ingest it, or run with the broker API as the source.");
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

        var series = PriceSeriesBuilder.Build(bars, symbol, start, end);
        return new LoadedPrices(series, source, PriceSeriesBuilder.SplitWarnings(series));
    }
}
