using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Tests;

/// <summary>The portfolio statistics against OpenAlgo's (portfolio_stats.fixtures.json, from record_portfolio_stats.py): headline metrics and every series the backtest tab draws.</summary>
public sealed class PortfolioStatsParityTests
{
    private static readonly JsonArray Cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portfolio_stats.fixtures.json")))!.AsArray();

    public static IEnumerable<object[]> Names() => Cases.Select(c => new object[] { c!["name"]!.GetValue<string>() });

    private static ReturnSeries Series(JsonNode dates, JsonNode values) => new(
        dates.AsArray().Select(d => DateOnly.ParseExact(d!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(), values.AsArray().Select(v => v!.GetValue<double>()).ToArray());

    /// <summary>The recorder's thinning: every 20th point plus the last, only past 40 points.</summary>
    private static JsonArray Sample(JsonArray rows, int step = 20)
    {
        if (rows.Count <= step * 2) return rows;
        var keep = new JsonArray();
        for (var i = 0; i < rows.Count; i += step) keep.Add(rows[i]!.DeepClone());
        if ((rows.Count - 1) % step != 0) keep.Add(rows[^1]!.DeepClone());
        return keep;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_statistics_match_OpenAlgo(string name)
    {
        var fixture = Cases.Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var returns = Series(fixture["dates"]!, fixture["returns"]!);
        var benchmark = fixture["benchmark"] is JsonArray b ? Series(fixture["dates"]!, b) : null;
        var rf = fixture["rf"]!.GetValue<double>();

        ToolsParityTests.AssertSame(fixture["metrics"], ReturnStats.Headline(returns, benchmark, rf), 1e-9);

        var series = ReturnStats.SeriesAnalytics(returns, rf, benchmark);
        foreach (var key in new[] { "drawdown", "weekly_returns" }) series[key] = Sample((JsonArray)series[key]!);
        if (series["rolling"] is JsonObject rolling)
            foreach (var key in new[] { "sharpe", "volatility" }) rolling[key] = Sample((JsonArray)rolling[key]!);
        ToolsParityTests.AssertSame(fixture["series"], series, 1e-9);
    }
}
