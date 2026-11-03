using System.Text.Json;
using EPTradeBot.Core.Risk;

namespace EPTradeBot.Core.Tests.Risk;

/// <summary>
/// OpenAlgo's test/risk/vectors.json is the contract between the Python risk core
/// and its TypeScript copy; the C# port is held to the same file, every key of every case.
/// </summary>
public sealed class RiskVectorTests
{
    private static readonly JsonDocument Vectors =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Risk", "vectors.json")));

    private static double Tolerance => Vectors.RootElement.GetProperty("tolerance").GetDouble();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors.RootElement.GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void The_contract_file_has_all_35_cases()
    {
        Assert.Equal(35, Vectors.RootElement.GetProperty("cases").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Matches_the_shared_vector(string name)
    {
        var c = Vectors.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var state = c.GetProperty("state").EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value));
        var ltpElement = c.GetProperty("ltp");
        double? ltp = ltpElement.ValueKind == JsonValueKind.Number ? ltpElement.GetDouble() : null;

        var decision = PositionEvaluator.Evaluate(PositionRisk.FromState(state), ltp);
        var expected = c.GetProperty("expected");

        Assert.Equal(expected.GetProperty("evaluated").GetBoolean(), decision.Evaluated);
        Assert.Equal(expected.GetProperty("breached").GetBoolean(), decision.Breached);
        var reason = expected.GetProperty("reason");
        Assert.Equal(reason.ValueKind == JsonValueKind.Null ? null : reason.GetString(), decision.Reason?.Wire());
        AssertNumber(expected.GetProperty("current_sl"), decision.StopPrice, "current_sl");
        AssertNumber(expected.GetProperty("highest_price"), decision.HighestPrice, "highest_price");
        AssertNumber(expected.GetProperty("lowest_price"), decision.LowestPrice, "lowest_price");
        Assert.Equal(expected.GetProperty("stop_moved").GetBoolean(), decision.StopMoved);
        Assert.Equal(expected.GetProperty("trail_armed").GetBoolean(), decision.TrailArmed);
        AssertNumber(expected.GetProperty("pnl"), decision.Pnl, "pnl");
    }

    private static void AssertNumber(JsonElement expected, double? actual, string field)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual is null, $"{field}: expected null, got {actual}");
            return;
        }

        Assert.True(actual is not null, $"{field}: expected {expected.GetDouble()}, got null");
        Assert.True(Math.Abs(expected.GetDouble() - actual!.Value) <= Tolerance, $"{field}: expected {expected.GetDouble()}, got {actual}");
    }

    private static object? ToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
