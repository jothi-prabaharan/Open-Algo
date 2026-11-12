using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Tests;

/// <summary>The per-strategy position book against OpenAlgo's (strategy_book.fixtures.json, from record_strategy_book.py): every fill's effect and the legs it leaves.</summary>
public sealed class StrategyBookParityTests
{
    private static readonly JsonArray Cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "strategy_book.fixtures.json")))!.AsArray();

    public static IEnumerable<object[]> Names() => Cases.Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(Names))]
    public void The_book_matches_OpenAlgo(string name)
    {
        var fixture = Cases.Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var ledger = new StrategyBookLedger();
        var today = "2026-11-02";
        var results = new JsonArray();
        foreach (var step in fixture["steps"]!.AsArray().Select(x => x!.AsObject()))
        {
            if (step["day"] is JsonValue day) today = day.GetValue<string>();
            if (step["op"]!.GetValue<string>() == "tag")
                results.Add(new JsonObject { ["op"] = "tag", ["ok"] = ledger.RecordTag(step["order"]!.GetValue<string>(), step["user"]!.GetValue<string>(), step["strategy"]!.GetValue<string>(), step["symbol"]!.GetValue<string>(), step["exchange"]!.GetValue<string>(), step["product"]!.GetValue<string>(), today) });
            else
                results.Add(new JsonObject { ["op"] = "fill", ["result"] = ledger.ApplyFill(step["order"]!.GetValue<string>(), step["qty"]!.GetValue<double>(), step["avg"]!.GetValue<double>(), step["action"]!.GetValue<string>(), today) });
        }

        ToolsParityTests.AssertSame(fixture["results"], results, 1e-9);
        var legs = new JsonArray(ledger.Legs(fixture["read_day"]!.GetValue<string>()).OfType<JsonObject>()
            .OrderBy(l => l["strategy"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(l => l["symbol"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(l => l["product"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(l => (JsonNode)l.DeepClone()).ToArray());
        ToolsParityTests.AssertSame(fixture["legs"], legs, 1e-9);
    }

    [Theory]
    [InlineData("2026-11-03 02:59:59", "2026-11-02")]
    [InlineData("2026-11-03 03:00:00", "2026-11-03")]
    [InlineData("2026-11-03 00:00:00", "2026-11-02")]
    [InlineData("2026-11-03 15:30:00", "2026-11-03")]
    public void The_session_rolls_over_at_three_in_the_morning_not_at_midnight(string at, string session) =>
        Assert.Equal(session, StrategyBookLedger.SessionDate(DateTime.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
}
