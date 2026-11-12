using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

/// <summary>A SWOT derived from a finished backtest report: each finding carries the figure that produced it. OpenAlgo's portfolio.insights.build_findings.</summary>
public static class PortfolioInsights
{
    private sealed record Finding(string Kind, string Tag, string Title, string Detail, double Severity, JsonObject Evidence);

    private static double? D(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    private static JsonObject Obj(JsonNode? n) => n as JsonObject ?? [];

    private static string Pct(double? value, int dp = 1) => value is null ? "n/a" : PyFormat.Fixed(value.Value * 100, dp) + "%";

    private static string F(double v, int dp) => PyFormat.Fixed(v, dp);

    public static JsonObject Build(JsonObject report)
    {
        var metrics = Obj(report["metrics"]);
        var health = Obj(report["health"]);
        var structure = Obj(report["structure"]);
        var attribution = Obj(report["attribution"]);
        var crisis = Obj(Obj(report["crisis"])["summary"]);
        var sweep = Obj(report["rebalancing_sweep"]);
        var costs = Obj(report["costs"]);
        var items = (report["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var o = new List<Finding>();

        var excess = D(metrics["excess_cagr"]);
        if (excess is { } ex)
        {
            if (ex < -0.01)
                o.Add(new("weakness", "Lagging the market", "Lagging the market",
                    $"The portfolio is behind its benchmark by {Pct(Math.Abs(ex))} a year. It compounded at {Pct(D(metrics["cagr"]))} against {Pct(D(metrics["benchmark_cagr"]))}.",
                    Math.Min(1.0, 0.5 + Math.Abs(ex) * 4), new JsonObject { ["excess_cagr"] = ex }));
            else if (ex > 0.01)
                o.Add(new("strength", "Beating the market", "Ahead of the benchmark",
                    $"The portfolio is ahead by {Pct(ex)} a year, compounding at {Pct(D(metrics["cagr"]))} against {Pct(D(metrics["benchmark_cagr"]))}.",
                    Math.Min(1.0, 0.5 + ex * 4), new JsonObject { ["excess_cagr"] = ex }));
        }

        if (D(metrics["sharpe"]) is { } sharpe)
        {
            if (sharpe < 0)
                o.Add(new("weakness", "Unrewarded risk", "Risk without reward", $"Sharpe is {F(sharpe, 2)}: the portfolio carried {Pct(D(metrics["volatility"]))} volatility and was not paid for it.", 0.9, new JsonObject { ["sharpe"] = sharpe }));
            else if (sharpe > 1.0)
                o.Add(new("strength", "Efficient", "Well paid for the risk taken", $"Sharpe is {F(sharpe, 2)} on {Pct(D(metrics["volatility"]))} volatility.", Math.Min(1.0, sharpe / 2), new JsonObject { ["sharpe"] = sharpe }));
        }

        if (D(metrics["up_capture"]) is { } up && D(metrics["down_capture"]) is { } down && down > 0)
        {
            if (down < up)
                o.Add(new("strength", "Defensive", "Falls less than it rises", $"It captures {F(up * 100, 0)}% of the benchmark's up moves and only {F(down * 100, 0)}% of its falls.", 0.6, new JsonObject { ["up_capture"] = up, ["down_capture"] = down }));
            else if (down > up + 0.1)
                o.Add(new("weakness", "Poor asymmetry", "Falls more than it rises", $"It takes {F(down * 100, 0)}% of the benchmark's falls but only {F(up * 100, 0)}% of its gains.", 0.8, new JsonObject { ["up_capture"] = up, ["down_capture"] = down }));
        }

        var bets = D(structure["effective_bets"]);
        var largest = D(structure["largest_cluster_weight"]);
        if (bets is { } nb && largest is { } lg && items.Count > 0)
        {
            if (lg > 0.5 && items.Count > 2)
            {
                var members = (structure["clusters"] as JsonArray ?? []).OfType<JsonObject>().Where(c => D(c["weight"]) == lg).Select(c => (c["members"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).ToList()).FirstOrDefault() ?? [];
                o.Add(new("threat", "Concentration", "Concentrated despite the holding count",
                    $"{Pct(lg, 0)} of the book moves as one position ({string.Join(", ", members.Take(4))}{(members.Count > 4 ? "..." : "")}). {items.Count} holdings behave like {(int)nb} independent bets.",
                    Math.Min(1.0, lg + 0.2), new JsonObject { ["largest_cluster_weight"] = lg, ["effective_bets"] = (int)nb }));
            }
            else if (nb >= Math.Max(3, items.Count * 0.7))
            {
                o.Add(new("strength", "Genuinely diversified", "Holdings move independently",
                    $"{items.Count} holdings behave like {(int)nb} independent bets, so a shock to one is unlikely to take the rest with it.", 0.5, new JsonObject { ["effective_bets"] = (int)nb }));
            }
        }

        var heaviest = items.Count == 0 ? null : items.Select((i, k) => (i, k)).OrderByDescending(x => D(x.i["weight_final"]) ?? 0).ThenBy(x => x.k).First().i;
        if (heaviest is not null && (D(heaviest["weight_final"]) ?? 0) > 0.4 && items.Count > 1)
        {
            var wf = D(heaviest["weight_final"]) ?? 0;
            o.Add(new("threat", "Single-name risk", "One holding dominates", $"{heaviest["symbol"]!.GetValue<string>()} is {Pct(wf, 0)} of the portfolio. Its result is close to the portfolio's result.",
                Math.Min(1.0, wf + 0.3), new JsonObject { ["symbol"] = heaviest["symbol"]!.GetValue<string>(), ["weight"] = wf }));
        }

        if (D(metrics["max_drawdown"]) is { } dd)
        {
            if (dd < -0.30)
                o.Add(new("threat", "Deep drawdown", "It has fallen a long way before", $"The worst peak-to-trough fall was {Pct(dd)}, which needs a {Pct(1 / (1 + dd) - 1)} gain simply to recover.", Math.Min(1.0, Math.Abs(dd) + 0.4), new JsonObject { ["max_drawdown"] = dd }));
            else if (dd > -0.15)
                o.Add(new("strength", "Shallow drawdowns", "Falls have stayed contained", $"The worst fall was {Pct(dd)}.", 0.4, new JsonObject { ["max_drawdown"] = dd }));
        }

        if (attribution["available"] is JsonValue av && av.TryGetValue<bool>(out var available) && available && attribution["holdings"] is JsonArray { Count: > 0 } rows)
        {
            var best = (JsonObject)rows[0]!;
            var worst = (JsonObject)rows[^1]!;
            var worstContribution = D(worst["contribution"]) ?? 0;
            if (worstContribution < -0.02)
                o.Add(new("weakness", "Wealth eroder", $"{worst["symbol"]!.GetValue<string>()} is costing the portfolio",
                    $"It returned {Pct(D(worst["return"]))} against the benchmark's {Pct(D(attribution["benchmark_return"]))}, taking {Pct(Math.Abs(worstContribution))} off the result at {Pct(D(worst["weight"]), 0)} weight.",
                    Math.Min(1.0, Math.Abs(worstContribution) * 3 + 0.4), new JsonObject { ["symbol"] = worst["symbol"]!.GetValue<string>(), ["contribution"] = worstContribution }));
            var bestContribution = D(best["contribution"]) ?? 0;
            if (bestContribution > 0.02 && (D(best["weight"]) ?? 1) < 0.15)
                o.Add(new("opportunity", "Underweight winner", $"{best["symbol"]!.GetValue<string>()} did well on a small position",
                    $"It returned {Pct(D(best["return"]))} but held only {Pct(D(best["weight"]), 0)} of the book. A larger position would have contributed more than the {Pct(bestContribution)} it did.",
                    0.6, new JsonObject { ["symbol"] = best["symbol"]!.GetValue<string>(), ["weight"] = D(best["weight"]) }));
            var selection = D(attribution["selection_effect"]);
            var allocation = D(attribution["allocation_effect"]);
            if (selection is { } sel && allocation is { } alloc && alloc < -0.02)
                o.Add(new("opportunity", "Sizing is costing you", "The weighting is working against the picks",
                    $"An equal-weighted version of the same holdings would have returned {Pct(D(attribution["equal_weight_return"]))} against the actual {Pct(D(attribution["portfolio_return"]))}. The picks added {Pct(sel)}; the sizing took {Pct(Math.Abs(alloc))} back.",
                    0.7, new JsonObject { ["allocation_effect"] = alloc }));
        }

        var variants = (sweep["variants"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var currentRule = Obj(report["meta"])["rule"]?.GetValue<string>();
        var bestLabel = sweep["best_by_sharpe"]?.GetValue<string>();
        if (variants.Count > 0 && bestLabel is not null)
        {
            var current = variants.FirstOrDefault(v => v["rule"]!.GetValue<string>() == currentRule);
            var bestVariant = variants.FirstOrDefault(v => v["label"]!.GetValue<string>() == bestLabel);
            if (current is not null && bestVariant is not null && bestVariant["label"]!.GetValue<string>() != current["label"]!.GetValue<string>())
            {
                var gain = D(bestVariant["sharpe"])!.Value - D(current["sharpe"])!.Value;
                if (gain > 0.02)
                    o.Add(new("opportunity", "Better rebalancing rule", $"{bestLabel} rebalancing suits this portfolio better",
                        $"It reaches a Sharpe of {F(D(bestVariant["sharpe"])!.Value, 2)} against {F(D(current["sharpe"])!.Value, 2)} today, with {Pct(D(bestVariant["cost_drag"]), 2)} of cost drag against {Pct(D(current["cost_drag"]), 2)}.",
                        0.55, new JsonObject { ["suggested"] = bestLabel, ["sharpe_gain"] = gain }));
            }
        }

        if (D(costs["drag"]) is { } drag && drag > 0.01)
            o.Add(new("weakness", "Cost drag", "Trading is eating the return", $"Costs took {Pct(drag, 2)} of the total return, on {Pct(D(costs["turnover"]), 0)} of turnover.", Math.Min(1.0, drag * 20 + 0.3), new JsonObject { ["cost_drag"] = drag }));

        var hit = D(crisis["hit_rate"]);
        var crisisCount = (int)(D(crisis["count"]) ?? 0);
        if (hit is { } h && crisisCount >= 3)
        {
            if (h >= 0.6)
                o.Add(new("strength", "Holds up in stress", "It has beaten the benchmark in most crises",
                    $"It outperformed in {F(h * 100, 0)}% of the {crisisCount} stress periods covered, averaging {Pct(D(crisis["average_return"]))}.", 0.6, new JsonObject { ["hit_rate"] = h }));
            else if (h <= 0.4)
                o.Add(new("threat", "Weak in stress", "It has lagged in most crises", $"It beat the benchmark in only {F(h * 100, 0)}% of the {crisisCount} stress periods covered.", 0.7, new JsonObject { ["hit_rate"] = h }));
        }

        foreach (var pillar in (health["pillars"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (D(pillar["score"]) is not { } score) continue;
            var label = pillar["label"]!.GetValue<string>();
            if (score < 30 && !o.Any(f => string.Equals(f.Tag, label, StringComparison.OrdinalIgnoreCase)))
                o.Add(new("weakness", label, $"{label} scores {F(score, 0)}/100", $"{pillar["comment"]!.GetValue<string>()}. Measured as: {pillar["formula"]!.GetValue<string>()}.", 0.4 + (30 - score) / 100,
                    new JsonObject { ["pillar"] = pillar["key"]!.GetValue<string>(), ["score"] = score }));
        }

        // Most severe first; equal severities keep the order they were found in.
        var sorted = o.Select((f, i) => (f, i)).OrderByDescending(x => x.f.Severity).ThenBy(x => x.i).Select(x => x.f).ToList();
        JsonNode Dict(Finding f) => new JsonObject { ["kind"] = f.Kind, ["tag"] = f.Tag, ["title"] = f.Title, ["detail"] = f.Detail, ["severity"] = f.Severity, ["evidence"] = f.Evidence.DeepClone() };
        JsonArray Of(string kind) => new(sorted.Where(f => f.Kind == kind).Select(Dict).ToArray());
        var grouped = new JsonObject { ["strengths"] = Of("strength"), ["weaknesses"] = Of("weakness"), ["opportunities"] = Of("opportunity"), ["threats"] = Of("threat") };
        var result = new JsonObject
        {
            ["headline"] = sorted.Count > 0 ? Dict(sorted[0]) : null,
            ["tags"] = new JsonArray(sorted.Take(6).Select(f => (JsonNode)new JsonObject { ["kind"] = f.Kind, ["label"] = f.Tag }).ToArray()),
        };
        foreach (var (k, v) in grouped) result[k] = v!.DeepClone();
        result["counts"] = new JsonObject(grouped.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ((JsonArray)kv.Value!).Count)));
        return result;
    }
}
