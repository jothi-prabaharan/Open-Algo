using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;

namespace EPTradeBot.Core.Tests;

public sealed class FlowValidatorTests
{
    private static JsonObject Flow(string nodes, string edges, string name = "demo") =>
        JsonNode.Parse($$"""{"name": "{{name}}", "nodes": {{nodes}}, "edges": {{edges}}}""")!.AsObject();

    private static string[] Codes(JsonNode? flow, bool strict = true, bool requireName = true, Func<string, bool>? supports = null) =>
        FlowValidator.Validate(flow, requireName, strict, supports).Select(e => e.Code).ToArray();

    private const string Simple = """[{"id":"s","type":"start"},{"id":"l","type":"log"}]""";

    [Fact]
    public void A_complete_workflow_passes() => Assert.Empty(Codes(Flow(Simple, """[{"source":"s","target":"l"}]""")));

    [Fact]
    public void The_shape_is_checked_even_while_a_graph_is_still_being_drawn()
    {
        Assert.Equal(["invalid_type"], Codes(JsonValue.Create("nope"), strict: false));
        Assert.Contains("required", Codes(JsonNode.Parse("""{"name":"x"}"""), strict: false));
        Assert.Contains("required", Codes(Flow(Simple, "[]", name: "  "), strict: false));
        Assert.DoesNotContain("required", Codes(Flow(Simple, "[]", name: "  "), strict: false, requireName: false));
        Assert.Contains("duplicate", Codes(Flow("""[{"id":"a","type":"start"},{"id":"a","type":"log"}]""", "[]"), strict: false));
        Assert.Contains("unknown_type", Codes(Flow("""[{"id":"a","type":"start"},{"id":"b","type":"teleport"}]""", "[]"), strict: false));
        Assert.Contains("dangling", Codes(Flow(Simple, """[{"source":"s","target":"ghost"}]"""), strict: false));
        Assert.Contains("dangling", Codes(Flow(Simple, """[{"source":"ghost","target":"l"}]"""), strict: false));
    }

    [Fact]
    public void A_half_built_graph_may_be_saved_but_not_run()
    {
        var half = Flow("""[{"id":"l","type":"log"}]""", "[]");
        Assert.Empty(Codes(half, strict: false));
        Assert.Contains("no_trigger", Codes(half));
    }

    [Fact]
    public void There_is_exactly_one_trigger() =>
        Assert.Contains("multiple_triggers", Codes(Flow("""[{"id":"a","type":"start"},{"id":"b","type":"webhookTrigger"}]""", "[]")));

    [Fact]
    public void A_cycle_is_reported_instead_of_running_to_the_visit_limit() =>
        Assert.Contains("cycle", Codes(Flow("""[{"id":"s","type":"start"},{"id":"a","type":"log"},{"id":"b","type":"log"}]""", """[{"source":"s","target":"a"},{"source":"a","target":"b"},{"source":"b","target":"a"}]""")));

    [Fact]
    public void A_node_the_trigger_cannot_reach_is_reported_but_a_drawn_group_is_not()
    {
        var nodes = """[{"id":"s","type":"start"},{"id":"a","type":"log"},{"id":"orphan","type":"log"},{"id":"g","type":"group"}]""";
        var errors = FlowValidator.Validate(Flow(nodes, """[{"source":"s","target":"a"}]"""), true, true);
        var unreachable = Assert.Single(errors);
        Assert.Equal("unreachable", unreachable.Code);
        Assert.Contains("orphan", unreachable.Message);
        Assert.EndsWith(": orphan", unreachable.Message); // only the orphan, not the group
    }

    [Fact]
    public void Limits_on_size_are_enforced()
    {
        var many = "[" + string.Join(",", Enumerable.Range(0, FlowValidator.MaxNodes + 1).Select(i => $$"""{"id":"n{{i}}","type":"log"}""")) + "]";
        Assert.Contains("too_large", Codes(Flow(many, "[]"), strict: false));
    }

    [Fact]
    public void A_node_type_the_editor_knows_but_the_engine_cannot_run_is_refused_by_name_when_running()
    {
        var flow = Flow("""[{"id":"s","type":"start"},{"id":"b","type":"basketOrder"},{"id":"x","type":"splitOrder"}]""", """[{"source":"s","target":"b"},{"source":"b","target":"x"}]""");
        var errors = FlowValidator.Validate(flow, true, true, t => t == "start");
        var error = errors.Single(e => e.Code == "unsupported");
        Assert.Contains("basketOrder, splitOrder", error.Message);
        // Saving a half-built graph is still allowed; the editor may know more node types than this engine runs.
        Assert.DoesNotContain("unsupported", Codes(flow, strict: false, supports: t => t == "start"));
    }

    [Fact]
    public void Every_node_type_the_engine_runs_is_one_the_editor_knows()
    {
        var runner = new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All()));
        foreach (var type in FlowValidator.KnownTypes.Where(runner.Supports).Concat(["andGate", "orGate", "notGate"]))
            Assert.Contains(type, FlowValidator.KnownTypes);
        Assert.All(new[] { "placeOrder", "smartOrder", "modifyOrder", "cancelOrder", "cancelAllOrders", "closePositions", "indicator", "priceCondition", "positionCheck", "fundCheck", "getQuote", "funds" },
            t => Assert.True(runner.Supports(t), t));
    }
}
