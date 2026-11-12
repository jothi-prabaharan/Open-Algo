using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;
using EPTradeBot.Core.Time;

namespace EPTradeBot.Core.Tests;

public sealed class FlowApiNodesTests
{
    /// <summary>A platform that answers from a script and remembers what it was asked.</summary>
    private sealed class FakeApi : IFlowApi
    {
        public List<(string Api, JsonObject Body)> Calls { get; } = [];

        public Dictionary<string, JsonObject> Answers { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Calls.Add((api, (JsonObject)body.DeepClone()));
            return Task.FromResult(Answers.TryGetValue(api, out var a) ? (JsonObject)a.DeepClone() : new JsonObject { ["status"] = "success" });
        }
    }

    private static JsonObject Ok(JsonObject? with = null)
    {
        var o = new JsonObject { ["status"] = "success" };
        foreach (var (k, v) in with ?? []) o[k] = v?.DeepClone();
        return o;
    }

    private static JsonObject Err(string message) => new() { ["status"] = "error", ["error"] = message, ["code"] = 400 };

    private static FlowNode N(string id, string type, JsonObject data) => new(id, type, data);

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - Ist.Offset;

    private static readonly FlowRunner Runner = new(CoreFlowNodes.All().Concat(ApiFlowNodes.All()));

    /// <summary>Runs one node after a start node, with a log node on each branch.</summary>
    private static async Task<(FlowRun Run, FlowOutcome Outcome)> RunAsync(FakeApi api, FlowNode node, JsonObject? vars = null, string tag = "my-flow")
    {
        var nodes = new List<FlowNode> { N("s", "start", []), node, N("t", "log", new() { ["message"] = "TRUE" }), N("f", "log", new() { ["message"] = "FALSE" }), N("p", "log", new() { ["message"] = "PASS" }) };
        var edges = new List<FlowEdge> { new("s", node.Id, ""), new(node.Id, "t", "true"), new(node.Id, "f", "false"), new(node.Id, "p", "") };
        var run = new FlowRun(() => Noon, host: new FlowHost(api, tag));
        foreach (var (k, v) in vars ?? []) run.Context.Set(k, v?.DeepClone());
        var outcome = await Runner.RunAsync(new FlowGraph(nodes, edges), run, default);
        return (run, outcome);
    }

    private static string[] Said(FlowRun run) => run.Logs.Where(l => l.Message.StartsWith("[LOG]")).Select(l => l.Message[6..]).ToArray();

    [Fact]
    public async Task A_place_order_node_sends_the_order_it_describes_under_the_flows_name()
    {
        var api = new FakeApi();
        api.Answers["placeorder"] = Ok(new() { ["orderid"] = "123" });
        var (run, outcome) = await RunAsync(api, N("o", "placeOrder", new() { ["symbol"] = "sbin", ["exchange"] = "NSE", ["action"] = "buy", ["quantity"] = "{{webhook.qty}}", ["outputVariable"] = "res" }),
            new() { ["webhook"] = new JsonObject { ["qty"] = 25 } });

        Assert.True(outcome.Succeeded);
        var (name, body) = Assert.Single(api.Calls);
        Assert.Equal("placeorder", name);
        Assert.Equal("SBIN", body["symbol"]!.GetValue<string>());
        Assert.Equal("BUY", body["action"]!.GetValue<string>());
        Assert.Equal(25, body["quantity"]!.GetValue<long>());
        Assert.Equal("MARKET", body["pricetype"]!.GetValue<string>());
        Assert.Equal("MIS", body["product"]!.GetValue<string>());
        Assert.Equal("my-flow", body["strategy"]!.GetValue<string>());
        Assert.Equal("123", run.Context.Get("res")!["orderid"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("NSE", "MIS")]
    [InlineData("NFO", "NRML")]
    [InlineData("MCX", "NRML")]
    [InlineData("BSE", "MIS")]
    public async Task The_product_defaults_to_what_the_segment_normally_takes_and_a_chosen_one_wins(string exchange, string expected)
    {
        var api = new FakeApi();
        await RunAsync(api, N("o", "placeOrder", new() { ["symbol"] = "X", ["exchange"] = exchange, ["quantity"] = 1 }));
        Assert.Equal(expected, api.Calls.Single().Body["product"]!.GetValue<string>());

        api = new FakeApi();
        await RunAsync(api, N("o", "placeOrder", new() { ["symbol"] = "X", ["exchange"] = exchange, ["quantity"] = 1, ["product"] = "CNC" }));
        Assert.Equal("CNC", api.Calls.Single().Body["product"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_node_with_no_exchange_trades_on_nse()
    {
        var api = new FakeApi();
        await RunAsync(api, N("o", "placeOrder", new() { ["symbol"] = "X", ["quantity"] = 1 }));
        Assert.Equal("NSE", api.Calls.Single().Body["exchange"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_node_strategy_tag_replaces_the_flows_name()
    {
        var api = new FakeApi();
        await RunAsync(api, N("o", "placeOrder", new() { ["symbol"] = "X", ["quantity"] = 1, ["strategyTag"] = "scalp-{{leg}}" }), new() { ["leg"] = "a" });
        Assert.Equal("scalp-a", api.Calls.Single().Body["strategy"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("quantity", "0", "quantity must be at least 1")]
    [InlineData("quantity", "abc", "quantity must be numeric")]
    [InlineData("action", "HOLD", "action must be one of")]
    [InlineData("exchange", "XYZ", "exchange must be one of")]
    [InlineData("priceType", "LIMIT", "LIMIT order needs a positive price")]
    [InlineData("product", "", "product must not be blank")]
    public async Task An_order_that_cannot_be_read_is_refused_before_anything_is_sent(string field, string value, string reason)
    {
        var api = new FakeApi();
        var data = new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["quantity"] = 1 };
        data[field] = value;
        var (run, outcome) = await RunAsync(api, N("o", "placeOrder", data));

        Assert.Empty(api.Calls);
        Assert.False(outcome.Succeeded);
        Assert.Contains("Place order has invalid runtime order data", outcome.Message);
        Assert.Contains(reason, outcome.Message);
        Assert.Empty(Said(run));
    }

    [Fact]
    public async Task A_stop_order_needs_its_trigger_price()
    {
        var api = new FakeApi();
        var (_, outcome) = await RunAsync(api, N("o", "placeOrder", new() { ["symbol"] = "X", ["quantity"] = 1, ["priceType"] = "SL-M" }));
        Assert.Empty(api.Calls);
        Assert.Contains("SL-M order needs a positive trigger price", outcome.Message);
    }

    [Fact]
    public async Task A_rejected_order_stops_its_branch_and_fails_the_run_with_the_brokers_reason()
    {
        var api = new FakeApi();
        api.Answers["placeorder"] = Err("Insufficient funds");
        var nodes = new List<FlowNode> { N("s", "start", []), N("o", "placeOrder", new() { ["symbol"] = "X", ["quantity"] = 1 }), N("hedge", "log", new() { ["message"] = "HEDGE" }) };
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        var outcome = await Runner.RunAsync(new FlowGraph(nodes, [new("s", "o", ""), new("o", "hedge", "")]), run, default);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Insufficient funds", outcome.Message);
        Assert.Empty(Said(run));
    }

    [Fact]
    public async Task An_order_held_for_approval_counts_as_sent_and_its_answer_is_kept()
    {
        var api = new FakeApi();
        api.Answers["placeorder"] = Ok(new() { ["message"] = "Order queued for approval in Action Center", ["mode"] = "semi_auto", ["pending_order_id"] = 7 });
        var (run, outcome) = await RunAsync(api, N("o", "placeOrder", new() { ["symbol"] = "X", ["quantity"] = 1, ["outputVariable"] = "r" }));
        Assert.True(outcome.Succeeded);
        Assert.Equal(7, run.Context.Get("r")!["pending_order_id"]!.GetValue<int>());
        Assert.Contains("PASS", Said(run));
    }

    [Fact]
    public async Task A_smart_order_carries_its_position_size_and_may_have_zero_quantity()
    {
        var api = new FakeApi();
        await RunAsync(api, N("o", "smartOrder", new() { ["symbol"] = "X", ["quantity"] = 0, ["positionSize"] = "5" }));
        var (name, body) = Assert.Single(api.Calls);
        Assert.Equal("placesmartorder", name);
        Assert.Equal(5, body["position_size"]!.GetValue<long>());
        Assert.Equal(0, body["quantity"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_modify_keeps_what_the_node_does_not_change_from_the_live_order()
    {
        var api = new FakeApi();
        api.Answers["orderstatus"] = Ok(new()
        {
            ["data"] = new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["action"] = "SELL", ["product"] = "NRML", ["pricetype"] = "LIMIT", ["quantity"] = 500, ["price"] = 100.5, ["trigger_price"] = 0 },
        });
        var (_, outcome) = await RunAsync(api, N("m", "modifyOrder", new() { ["orderId"] = "9", ["newPrice"] = "101.25" }));

        Assert.True(outcome.Succeeded);
        var body = api.Calls.Single(c => c.Api == "modifyorder").Body;
        Assert.Equal("SELL", body["action"]!.GetValue<string>());
        Assert.Equal("NRML", body["product"]!.GetValue<string>());
        Assert.Equal("LIMIT", body["pricetype"]!.GetValue<string>());
        Assert.Equal(500, body["quantity"]!.GetValue<long>());
        Assert.Equal(101.25, body["price"]!.GetValue<double>());
        Assert.Equal("9", body["orderid"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("orderstatus_fails")]
    [InlineData("no_order_id")]
    [InlineData("order_missing_a_field")]
    [InlineData("zero_quantity")]
    public async Task A_modify_that_cannot_be_made_safely_is_not_sent(string why)
    {
        var api = new FakeApi();
        var live = new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["action"] = "BUY", ["product"] = "MIS", ["pricetype"] = "LIMIT", ["quantity"] = 10, ["price"] = 1 };
        var data = new JsonObject { ["orderId"] = "9" };
        switch (why)
        {
            case "orderstatus_fails": api.Answers["orderstatus"] = Err("not found"); break;
            case "no_order_id": data = []; break;
            case "order_missing_a_field": live.Remove("product"); api.Answers["orderstatus"] = Ok(new() { ["data"] = live }); break;
            default: api.Answers["orderstatus"] = Ok(new() { ["data"] = live }); data["newQuantity"] = "-5"; break;
        }

        var (_, outcome) = await RunAsync(api, N("m", "modifyOrder", data));
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain(api.Calls, c => c.Api == "modifyorder");
    }

    [Fact]
    public async Task Cancel_nodes_cancel_one_order_or_all_of_them()
    {
        var api = new FakeApi();
        await RunAsync(api, N("c", "cancelOrder", new() { ["orderId"] = "{{id}}" }), new() { ["id"] = "55" });
        Assert.Equal(("cancelorder", "55"), (api.Calls[0].Api, api.Calls[0].Body["orderid"]!.GetValue<string>()));
        api = new FakeApi();
        await RunAsync(api, N("c", "cancelAllOrders", []));
        Assert.Equal("cancelallorder", api.Calls.Single().Api);
    }

    [Fact]
    public async Task Close_positions_without_a_symbol_closes_everything()
    {
        var api = new FakeApi();
        await RunAsync(api, N("c", "closePositions", new() { ["symbol"] = "" }));
        Assert.Equal("closeposition", api.Calls.Single().Api);
    }

    [Theory]
    [InlineData(50, "SELL")]
    [InlineData(-30, "BUY")]
    public async Task Close_positions_with_a_symbol_squares_off_only_that_position_the_other_way(int held, string action)
    {
        var api = new FakeApi();
        api.Answers["positionbook"] = Ok(new()
        {
            ["data"] = new JsonArray(
                new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["product"] = "NRML", ["quantity"] = 7 },
                new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["product"] = "MIS", ["quantity"] = held },
                new JsonObject { ["symbol"] = "TCS", ["exchange"] = "NSE", ["product"] = "MIS", ["quantity"] = 9 }),
        });
        await RunAsync(api, N("c", "closePositions", new() { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["product"] = "MIS" }));
        var order = api.Calls.Single(c => c.Api == "placeorder").Body;
        Assert.Equal((action, Math.Abs(held), "SBIN", "MARKET"), (order["action"]!.GetValue<string>(), (int)order["quantity"]!.GetValue<long>(), order["symbol"]!.GetValue<string>(), order["pricetype"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Close_positions_with_a_symbol_and_nothing_held_sends_no_order()
    {
        var api = new FakeApi();
        api.Answers["positionbook"] = Ok(new() { ["data"] = new JsonArray() });
        var (_, outcome) = await RunAsync(api, N("c", "closePositions", new() { ["symbol"] = "SBIN" }));
        Assert.True(outcome.Succeeded);
        Assert.DoesNotContain(api.Calls, c => c.Api == "placeorder");
    }

    [Fact]
    public async Task Data_nodes_keep_the_answer_in_the_named_variable_for_later_nodes()
    {
        var api = new FakeApi();
        api.Answers["quotes"] = Ok(new() { ["data"] = new JsonObject { ["ltp"] = 101.5 } });
        var nodes = new List<FlowNode>
        {
            N("s", "start", []), N("q", "getQuote", new() { ["symbol"] = "SBIN", ["outputVariable"] = "quote" }), N("l", "log", new() { ["message"] = "ltp={{quote.data.ltp}}" }),
        };
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        await Runner.RunAsync(new FlowGraph(nodes, [new("s", "q", ""), new("q", "l", "")]), run, default);
        Assert.Equal(["ltp=101.5"], Said(run));
        Assert.Equal(("quotes", "NSE"), (api.Calls.Single().Api, api.Calls.Single().Body["exchange"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("orderBook", "orderbook")]
    [InlineData("tradeBook", "tradebook")]
    [InlineData("positionBook", "positionbook")]
    [InlineData("holdings", "holdings")]
    [InlineData("funds", "funds")]
    public async Task The_books_and_funds_nodes_call_their_endpoints(string type, string api)
    {
        var fake = new FakeApi();
        await RunAsync(fake, N("b", type, []));
        Assert.Equal(api, fake.Calls.Single().Api);
    }

    [Fact]
    public async Task Open_position_reads_the_quantity_and_profit_of_the_matching_position()
    {
        var api = new FakeApi();
        api.Answers["positionbook"] = Ok(new()
        {
            ["data"] = new JsonArray(
                new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["product"] = "NRML", ["quantity"] = 3, ["pnl"] = 1.0 },
                new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["product"] = "MIS", ["quantity"] = 10, ["pnl"] = 12.5 }),
        });
        var (run, _) = await RunAsync(api, N("p", "openPosition", new() { ["symbol"] = "SBIN", ["outputVariable"] = "pos" }));
        Assert.Equal((10, 12.5), (run.Context.Get("pos")!["quantity"]!.GetValue<int>(), run.Context.Get("pos")!["pnl"]!.GetValue<double>()));
    }

    [Theory]
    [InlineData("ltp", ">", "100", true)]
    [InlineData("ltp", "<", "100", false)]
    [InlineData("open", "==", "99", true)]
    [InlineData("high", ">=", "{{lvl}}", true)]
    [InlineData("prev_close", "!=", "100", false)]
    [InlineData("change_percent", ">=", "2", true)]
    [InlineData("change_percent", ">", "2", false)]
    public async Task A_price_condition_compares_the_chosen_quote_field(string field, string op, string value, bool expected)
    {
        var api = new FakeApi();
        api.Answers["quotes"] = Ok(new() { ["data"] = new JsonObject { ["ltp"] = 102.0, ["open"] = 99.0, ["high"] = 103.0, ["low"] = 98.0, ["prev_close"] = 100.0 } });
        var (run, outcome) = await RunAsync(api, N("c", "priceCondition", new() { ["symbol"] = "SBIN", ["field"] = field, ["operator"] = op, ["value"] = value }), new() { ["lvl"] = 103 });
        Assert.True(outcome.Succeeded);
        Assert.Equal([expected ? "TRUE" : "FALSE", "PASS"], Said(run));
    }

    [Theory]
    [InlineData("bogus", ">", "field 'bogus' is not one of")]
    [InlineData("ltp", "~", "operator '~' is not one of")]
    public async Task A_price_condition_that_cannot_be_read_takes_neither_branch(string field, string op, string reason)
    {
        var api = new FakeApi();
        var (run, outcome) = await RunAsync(api, N("c", "priceCondition", new() { ["symbol"] = "X", ["field"] = field, ["operator"] = op, ["value"] = 1 }));
        Assert.False(outcome.Succeeded);
        Assert.Contains(reason, outcome.Message);
        Assert.Equal(["PASS"], Said(run));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task A_price_condition_does_not_decide_on_a_quote_it_could_not_read()
    {
        var api = new FakeApi();
        api.Answers["quotes"] = Err("session expired");
        var (run, outcome) = await RunAsync(api, N("c", "priceCondition", new() { ["symbol"] = "X", ["field"] = "ltp", ["operator"] = "<", ["value"] = 1 }));
        Assert.False(outcome.Succeeded);
        Assert.Contains("session expired", outcome.Message);
        Assert.Equal(["PASS"], Said(run));
    }

    [Theory]
    [InlineData("exists", 0, 0.0, false)]
    [InlineData("exists", 5, 0.0, true)]
    [InlineData("not_exists", 0, 0.0, true)]
    [InlineData("quantity_above", 5, 4.0, true)]
    [InlineData("quantity_above", 5, 5.0, false)]
    [InlineData("quantity_below", 5, 4.0, false)]
    [InlineData("pnl_above", 5, 100.0, true)]
    [InlineData("pnl_below", 5, 100.0, false)]
    public async Task A_position_check_applies_the_chosen_condition(string condition, int quantity, double threshold, bool expected)
    {
        var api = new FakeApi();
        api.Answers["positionbook"] = Ok(new() { ["data"] = new JsonArray(new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["product"] = "MIS", ["quantity"] = quantity, ["pnl"] = 150.0 }) });
        var (run, _) = await RunAsync(api, N("c", "positionCheck", new() { ["symbol"] = "SBIN", ["condition"] = condition, ["threshold"] = threshold }));
        Assert.Equal([expected ? "TRUE" : "FALSE", "PASS"], Said(run));
    }

    [Fact]
    public async Task A_position_check_with_no_symbol_fails_closed()
    {
        var api = new FakeApi();
        var (run, outcome) = await RunAsync(api, N("c", "positionCheck", new() { ["condition"] = "not_exists" }));
        Assert.False(outcome.Succeeded);
        Assert.Contains("no symbol set", outcome.Message);
        Assert.Equal(["PASS"], Said(run));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task A_position_check_does_not_decide_on_a_book_it_could_not_read()
    {
        var api = new FakeApi();
        api.Answers["positionbook"] = Err("broker down");
        var (run, outcome) = await RunAsync(api, N("c", "positionCheck", new() { ["symbol"] = "X", ["condition"] = "not_exists" }));
        Assert.False(outcome.Succeeded);
        Assert.Equal(["PASS"], Said(run));
    }

    [Theory]
    [InlineData(50000, true)]
    [InlineData(100000, true)]
    [InlineData(100000.5, false)]
    public async Task A_fund_check_passes_when_the_available_cash_reaches_the_minimum(double minimum, bool expected)
    {
        var api = new FakeApi();
        api.Answers["funds"] = Ok(new() { ["data"] = new JsonObject { ["availablecash"] = "100000.00" } });
        var (run, _) = await RunAsync(api, N("c", "fundCheck", new() { ["minAvailable"] = minimum }));
        Assert.Equal([expected ? "TRUE" : "FALSE", "PASS"], Said(run));
    }

    [Fact]
    public async Task A_fund_check_with_no_minimum_fails_closed_and_a_legacy_less_than_is_refused()
    {
        var api = new FakeApi();
        api.Answers["funds"] = Ok(new() { ["data"] = new JsonObject { ["availablecash"] = 1 } });
        var (run, outcome) = await RunAsync(api, N("c", "fundCheck", []));
        Assert.False(outcome.Succeeded);
        Assert.Contains("no Minimum Available", outcome.Message);
        Assert.Equal(["PASS"], Said(run));

        (run, outcome) = await RunAsync(api, N("c", "fundCheck", new() { ["threshold"] = 10, ["operator"] = "lt" }));
        Assert.False(outcome.Succeeded);
        Assert.Contains("legacy 'lt' comparison", outcome.Message);
        Assert.Empty(api.Calls);

        (run, outcome) = await RunAsync(api, N("c", "fundCheck", new() { ["threshold"] = 0.5 }));
        Assert.Equal(["TRUE", "PASS"], Said(run));
        Assert.Contains(run.Logs, l => l.Level == "warning" && l.Message.Contains("legacy threshold"));
    }

    [Fact]
    public async Task A_fund_check_does_not_decide_on_funds_it_could_not_read()
    {
        var api = new FakeApi();
        api.Answers["funds"] = Err("session expired");
        var (run, outcome) = await RunAsync(api, N("c", "fundCheck", new() { ["minAvailable"] = 1 }));
        Assert.False(outcome.Succeeded);
        Assert.Equal(["PASS"], Said(run));
    }

    [Fact]
    public async Task A_workflow_with_a_node_this_platform_cannot_run_is_not_started_at_all()
    {
        var api = new FakeApi();
        var nodes = new List<FlowNode> { N("s", "start", []), N("o", "placeOrder", new() { ["symbol"] = "X", ["quantity"] = 1 }), N("b", "basketOrder", []), N("u", "madeUp", []) };
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        var outcome = await Runner.RunAsync(new FlowGraph(nodes, [new("s", "o", ""), new("o", "b", "")]), run, default);
        Assert.False(outcome.Succeeded);
        Assert.Contains("cannot run basketOrder nodes yet", outcome.Message);
        Assert.Empty(api.Calls);
    }
}

public sealed class FlowMarketInfoNodeTests
{
    private sealed class Api : IFlowApi
    {
        public List<(string Api, JsonObject Body)> Calls { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Calls.Add((api, (JsonObject)body.DeepClone()));
            return Task.FromResult(new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["api"] = api } });
        }
    }

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - EPTradeBot.Core.Time.Ist.Offset;

    private static async Task<(Api Api, FlowRun Run, FlowOutcome Outcome)> RunAsync(string type, JsonObject data, JsonObject? vars = null)
    {
        var api = new Api();
        data["outputVariable"] = "out";
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        foreach (var (k, v) in vars ?? []) run.Context.Set(k, v?.DeepClone());
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph([new("s", "start", []), new("n", type, data)], [new("s", "n", "")]), run, default);
        return (api, run, outcome);
    }

    [Fact]
    public async Task Each_lookup_node_asks_for_what_it_describes_and_keeps_the_answer()
    {
        var (api, run, outcome) = await RunAsync("symbol", new() { ["symbol"] = "{{s}}", ["exchange"] = "BSE" }, new() { ["s"] = "SBIN" });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(("symbol", "SBIN", "BSE"), (api.Calls[0].Api, api.Calls[0].Body["symbol"]!.GetValue<string>(), api.Calls[0].Body["exchange"]!.GetValue<string>()));
        Assert.Equal("symbol", run.Context.Get("out")!["data"]!["api"]!.GetValue<string>());

        (api, _, _) = await RunAsync("optionSymbol", []);
        Assert.Equal("""{"underlying":"NIFTY","exchange":"NSE_INDEX","expiry_date":"","offset":"ATM","option_type":"CE"}""", api.Calls[0].Body.ToJsonString());

        (api, _, _) = await RunAsync("expiry", new() { ["instrumenttype"] = "FUTURES" });
        Assert.Equal("""{"symbol":"NIFTY","exchange":"NFO","instrumenttype":"futures"}""", api.Calls[0].Body.ToJsonString());
        (api, _, _) = await RunAsync("expiry", new() { ["instrumenttype"] = "swaps" });
        Assert.Equal("options", api.Calls[0].Body["instrumenttype"]!.GetValue<string>());

        (api, _, _) = await RunAsync("intervals", []);
        Assert.Equal(("intervals", "{}"), (api.Calls[0].Api, api.Calls[0].Body.ToJsonString()));

        (api, _, _) = await RunAsync("syntheticFuture", new() { ["expiryDate"] = "26NOV26" });
        Assert.Equal("""{"underlying":"NIFTY","exchange":"NSE_INDEX","expiry_date":"26NOV26"}""", api.Calls[0].Body.ToJsonString());
    }

    [Fact]
    public async Task Multi_quotes_splits_the_comma_list_and_drops_blanks()
    {
        var (api, _, _) = await RunAsync("multiQuotes", new() { ["symbols"] = " SBIN, ,INFY ,", ["exchange"] = "NSE" });
        Assert.Equal("""{"symbols":[{"symbol":"SBIN","exchange":"NSE"},{"symbol":"INFY","exchange":"NSE"}]}""", api.Calls[0].Body.ToJsonString());
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("7", 7)]
    [InlineData("{{n}}", 12)]
    [InlineData("many", 10)]
    public async Task The_option_chain_strike_count_is_read_the_way_OpenAlgo_reads_an_integer(string? given, int expected)
    {
        var data = new JsonObject { ["expiryDate"] = "26NOV26" };
        if (given is not null) data["strikeCount"] = given;
        var (api, _, _) = await RunAsync("optionChain", data, new() { ["n"] = 12 });
        Assert.Equal(expected, api.Calls[0].Body["strike_count"]!.GetValue<long>());
    }

    [Fact]
    public async Task Holidays_ask_for_the_current_year_unless_one_is_given_and_timings_default_to_today_in_india()
    {
        var (api, _, _) = await RunAsync("holidays", []);
        Assert.Equal("{}", api.Calls[0].Body.ToJsonString());
        (api, _, _) = await RunAsync("holidays", new() { ["year"] = 2026 });
        Assert.Equal(2026, api.Calls[0].Body["year"]!.GetValue<long>());
        (api, _, _) = await RunAsync("timings", []);
        Assert.Equal("2026-11-11", api.Calls[0].Body["date"]!.GetValue<string>());
        (api, _, _) = await RunAsync("timings", new() { ["date"] = "2026-12-25" });
        Assert.Equal("2026-12-25", api.Calls[0].Body["date"]!.GetValue<string>());
    }
}
