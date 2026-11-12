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
        // An engine without the API nodes: whichever node types the platform gains, this engine cannot run an order node it was not given.
        var outcome = await new FlowRunner(CoreFlowNodes.All()).RunAsync(new FlowGraph(nodes, [new("s", "o", ""), new("o", "b", "")]), run, default);
        Assert.False(outcome.Succeeded);
        Assert.Contains("cannot run basketOrder, placeOrder nodes yet", outcome.Message);
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

    [Fact]
    public async Task A_split_order_sends_the_order_with_its_split_size_under_the_flows_name()
    {
        var (api, _, outcome) = await RunAsync("splitOrder", new() { ["symbol"] = "sbin", ["quantity"] = "{{q}}", ["action"] = "SELL", ["splitSize"] = 25 }, new() { ["q"] = 100 });
        Assert.True(outcome.Succeeded, outcome.Message);
        var (name, body) = Assert.Single(api.Calls);
        Assert.Equal("splitorder", name);
        Assert.Equal(("SBIN", "NSE", "SELL", 100L, 25L, "MARKET", "MIS", "t"), (body["symbol"]!.GetValue<string>(), body["exchange"]!.GetValue<string>(), body["action"]!.GetValue<string>(),
            body["quantity"]!.GetValue<long>(), body["splitsize"]!.GetValue<long>(), body["pricetype"]!.GetValue<string>(), body["product"]!.GetValue<string>(), body["strategy"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(0, "splitSize")]
    [InlineData(-3, "splitSize")]
    public async Task A_split_size_below_one_stops_the_node_before_anything_is_sent(int size, string field)
    {
        var (api, run, outcome) = await RunAsync("splitOrder", new() { ["symbol"] = "SBIN", ["quantity"] = 10, ["splitSize"] = size });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Level == "error" && l.Message.Contains(field));
    }
}

public sealed class FlowOptionsOrderNodeTests
{
    private sealed class Api(params string[] expiries) : IFlowApi
    {
        public List<(string Api, JsonObject Body)> Calls { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Calls.Add((api, (JsonObject)body.DeepClone()));
            return Task.FromResult(api == "expiry"
                ? new JsonObject { ["status"] = "success", ["data"] = new JsonArray(expiries.Select(e => (JsonNode)e).ToArray()) }
                : new JsonObject { ["status"] = "success" });
        }
    }

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - EPTradeBot.Core.Time.Ist.Offset;

    private static async Task<(Api Api, FlowRun Run, FlowOutcome Outcome)> RunAsync(JsonObject data, LotSizeAnswer? lot = null, string[]? expiries = null)
    {
        var api = new Api(expiries ?? ["13-NOV-26", "20-NOV-26", "27-NOV-26", "29-DEC-26"]);
        var host = new FlowHost(api, "t") { LotSizes = (_, _, _) => Task.FromResult(lot ?? LotSizeAnswer.Found(75)) };
        var run = new FlowRun(() => Noon, host: host);
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph([new("s", "start", []), new("n", "optionsOrder", data)], [new("s", "n", "")]), run, default);
        return (api, run, outcome);
    }

    [Fact]
    public async Task Lots_become_units_the_expiry_is_looked_up_and_the_order_goes_to_the_options_endpoint()
    {
        var (api, _, outcome) = await RunAsync(new() { ["underlying"] = "nifty", ["quantity"] = 2, ["offset"] = "otm2", ["optionType"] = "pe", ["action"] = "sell" });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("expiry", api.Calls[0].Api);
        Assert.Equal("""{"symbol":"NIFTY","exchange":"NFO","instrumenttype":"options"}""", api.Calls[0].Body.ToJsonString());
        var (name, body) = api.Calls[1];
        Assert.Equal("optionsorder", name);
        Assert.Equal("""{"underlying":"NIFTY","exchange":"NSE_INDEX","action":"SELL","quantity":150,"expiry_date":"13NOV26","offset":"OTM2","option_type":"PE","pricetype":"MARKET","product":"NRML","price":0,"trigger_price":0,"splitsize":0,"strategy":"t"}""", body.ToJsonString());
    }

    [Theory]
    [InlineData("next_week", "20NOV26")]
    [InlineData("current_month", "27NOV26")]
    [InlineData("next_month", "29DEC26")]
    public async Task A_relative_expiry_picks_the_nearest_second_nearest_or_last_of_the_month(string type, string expected)
    {
        var (api, _, _) = await RunAsync(new() { ["expiryType"] = type });
        Assert.Equal(expected, api.Calls[1].Body["expiry_date"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_named_expiry_wins_and_nothing_is_looked_up()
    {
        var (api, _, _) = await RunAsync(new() { ["expiryType"] = "26dec26" });
        var call = Assert.Single(api.Calls);
        Assert.Equal(("optionsorder", "26DEC26"), (call.Api, call.Body["expiry_date"]!.GetValue<string>()));
        (api, _, _) = await RunAsync(new() { ["expiryDate"] = "01JAN27", ["expiryType"] = "next_week" });
        Assert.Equal("01JAN27", Assert.Single(api.Calls).Body["expiry_date"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("SENSEX", "BSE_INDEX")]
    [InlineData("GOLDM", "MCX")]
    [InlineData("RELIANCE", "NSE_INDEX")]
    public async Task The_underlying_decides_where_it_is_quoted(string underlying, string quote)
    {
        var (api, _, _) = await RunAsync(new() { ["underlying"] = underlying, ["expiryDate"] = "26NOV26" });
        Assert.Equal(quote, api.Calls[0].Body["exchange"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("expiryDate", "tomorrow", "DDMMMYY")]
    [InlineData("offset", "OTM51", "offset must be ATM")]
    [InlineData("optionType", "XX", "optionType must be one of")]
    [InlineData("expiryType", "someday", "expiryType must be one of")]
    [InlineData("quantity", 0, "quantity")]
    public async Task A_bad_field_stops_the_node_before_anything_is_sent(string key, object value, string words)
    {
        var data = new JsonObject { [key] = value is int i ? JsonValue.Create(i) : JsonValue.Create(value.ToString()) };
        var (api, run, outcome) = await RunAsync(data);
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Level == "error" && l.Message.Contains(words));
    }

    [Theory]
    [InlineData(LotSizeProblem.NotListed, "has no contract on NFO in the master contract")]
    [InlineData(LotSizeProblem.Unreadable, "Refusing to size an order on a guess")]
    public async Task An_unknown_lot_size_refuses_the_order_rather_than_guessing(LotSizeProblem problem, string words)
    {
        var (api, run, outcome) = await RunAsync(new(), new LotSizeAnswer(null, problem));
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Message.Contains(words));
    }

    [Fact]
    public async Task With_no_master_contract_loaded_only_an_underlying_with_a_built_in_lot_size_goes_ahead()
    {
        var (api, run, outcome) = await RunAsync(new() { ["expiryDate"] = "26NOV26" }, new LotSizeAnswer(null, LotSizeProblem.ExchangeEmpty));
        Assert.True(outcome.Succeeded);
        Assert.Equal(65, api.Calls[0].Body["quantity"]!.GetValue<long>());
        Assert.Contains(run.Logs, l => l.Level == "warning");
        (api, _, outcome) = await RunAsync(new() { ["underlying"] = "RELIANCE", ["expiryDate"] = "26NOV26" }, new LotSizeAnswer(null, LotSizeProblem.ExchangeEmpty));
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task No_matching_expiry_stops_the_node()
    {
        var (api, _, outcome) = await RunAsync(new() { ["expiryType"] = "next_month" }, expiries: ["13-NOV-26"]);
        Assert.False(outcome.Succeeded);
        Assert.Single(api.Calls);
    }
}

public sealed class FlowOptionsMultiOrderNodeTests
{
    private sealed class Api : IFlowApi
    {
        public List<(string Api, JsonObject Body)> Calls { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Calls.Add((api, (JsonObject)body.DeepClone()));
            return Task.FromResult(api == "expiry"
                ? new JsonObject { ["status"] = "success", ["data"] = new JsonArray("13-NOV-26", "20-NOV-26", "27-NOV-26") }
                : new JsonObject { ["status"] = "success" });
        }
    }

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - EPTradeBot.Core.Time.Ist.Offset;

    private static async Task<(Api Api, FlowRun Run, FlowOutcome Outcome)> RunAsync(JsonObject data, JsonObject? vars = null)
    {
        var api = new Api();
        var host = new FlowHost(api, "t") { LotSizes = (_, _, _) => Task.FromResult(LotSizeAnswer.Found(75)) };
        var run = new FlowRun(() => Noon, host: host);
        foreach (var (k, v) in vars ?? []) run.Context.Set(k, v?.DeepClone());
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph([new("s", "start", []), new("n", "optionsMultiOrder", data)], [new("s", "n", "")]), run, default);
        return (api, run, outcome);
    }

    private static string Legs(Api api) => api.Calls.Single(c => c.Api == "optionsmultiorder").Body["legs"]!.ToJsonString();

    [Fact]
    public async Task A_straddle_sells_the_money_call_and_put_in_units_of_the_lot()
    {
        var (api, _, outcome) = await RunAsync(new() { ["strategy"] = "straddle", ["quantity"] = 2, ["expiryDate"] = "26NOV26" });
        Assert.True(outcome.Succeeded, outcome.Message);
        var call = Assert.Single(api.Calls);
        Assert.Equal("""{"underlying":"NIFTY","exchange":"NSE_INDEX","expiry_date":"26NOV26","legs":[{"offset":"ATM","option_type":"CE","action":"SELL","quantity":150,"pricetype":"MARKET","product":"NRML","price":0,"splitsize":0},{"offset":"ATM","option_type":"PE","action":"SELL","quantity":150,"pricetype":"MARKET","product":"NRML","price":0,"splitsize":0}],"strategy":"t"}""", call.Body.ToJsonString());
    }

    [Theory]
    [InlineData("strangle", "OTM2,CE,SELL;OTM2,PE,SELL")]
    [InlineData("iron_condor", "OTM2,CE,SELL;OTM4,CE,BUY;OTM2,PE,SELL;OTM4,PE,BUY")]
    [InlineData("bull_call_spread", "ATM,CE,BUY;OTM2,CE,SELL")]
    [InlineData("bear_put_spread", "ATM,PE,BUY;OTM2,PE,SELL")]
    public async Task Each_named_strategy_has_the_legs_it_is_known_by(string strategy, string expected)
    {
        var (api, _, _) = await RunAsync(new() { ["strategy"] = strategy, ["expiryDate"] = "26NOV26" });
        var legs = api.Calls.Single().Body["legs"]!.AsArray().Select(l => $"{l!["offset"]},{l["option_type"]},{l["action"]}");
        Assert.Equal(expected, string.Join(";", legs));
    }

    [Fact]
    public async Task A_strangle_uses_its_width_and_a_strategy_alias_is_read()
    {
        var (api, _, _) = await RunAsync(new() { ["strategyType"] = "strangle", ["strangleWidth"] = "otm5", ["action"] = "BUY", ["expiryDate"] = "26NOV26" });
        Assert.All(api.Calls.Single().Body["legs"]!.AsArray(), l => { Assert.Equal("OTM5", l!["offset"]!.GetValue<string>()); Assert.Equal("BUY", l["action"]!.GetValue<string>()); });
    }

    [Fact]
    public async Task Custom_legs_pick_a_strike_or_an_offset_and_may_carry_their_own_expiry_and_price()
    {
        var legs = JsonNode.Parse("""[{"strikeMode":"STRIKE","strike":24000,"expiry":"26nov26","optionType":"ce","action":"buy","quantity":1},{"offset":"otm3","expiryType":"next_week","optionType":"PE","action":"SELL","quantity":2,"priceType":"LIMIT","price":55.5,"splitSize":3}]""")!;
        var (api, _, outcome) = await RunAsync(new() { ["strategy"] = "custom", ["legs"] = legs, ["expiryDate"] = "13NOV26" });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("expiry", api.Calls[0].Api);   // only the relative leg expiry is looked up
        Assert.Equal("""[{"strike":24000,"option_type":"CE","action":"BUY","pricetype":"MARKET","product":"NRML","price":0,"trigger_price":0,"splitsize":0,"quantity":75,"expiry_date":"26NOV26"},{"offset":"OTM3","option_type":"PE","action":"SELL","pricetype":"LIMIT","product":"NRML","price":55.5,"trigger_price":0,"splitsize":3,"quantity":150,"expiry_date":"20NOV26"}]""", Legs(api));
    }

    [Fact]
    public async Task Legs_can_come_from_a_variable()
    {
        var legs = JsonNode.Parse("""[{"offset":"ATM","optionType":"CE","action":"SELL","quantity":1}]""")!;
        var (api, _, outcome) = await RunAsync(new() { ["strategy"] = "custom", ["legs"] = "{{plan.legs}}", ["expiryDate"] = "26NOV26" }, new() { ["plan"] = new JsonObject { ["legs"] = legs } });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Single(api.Calls.Single().Body["legs"]!.AsArray());
    }

    [Theory]
    [InlineData("""[]""", "legs must resolve to a non-empty list")]
    [InlineData("""[1]""", "every resolved leg must be an object")]
    [InlineData("""[{"offset":"ATM","optionType":"CE","action":"SELL","quantity":0}]""", "leg 1: quantity must be at least 1")]
    [InlineData("""[{"offset":"ATM","optionType":"CE","action":"SELL","quantity":1},{"offset":"XTM","optionType":"CE","action":"SELL","quantity":1}]""", "leg 2: offset must be ATM")]
    [InlineData("""[{"strikeMode":"STRIKE","strike":-5,"optionType":"CE","action":"SELL","quantity":1}]""", "leg 1: strike must be a positive number")]
    [InlineData("""[{"offset":"ATM","expiry":"soon","optionType":"CE","action":"SELL","quantity":1}]""", "leg 1: expiry must be in DDMMMYY format")]
    [InlineData("""[{"offset":"ATM","priceType":"LIMIT","optionType":"CE","action":"SELL","quantity":1}]""", "leg 1: LIMIT order needs a positive price")]
    public async Task A_bad_custom_leg_stops_the_node_naming_the_leg(string legs, string words)
    {
        var (api, run, outcome) = await RunAsync(new() { ["strategy"] = "custom", ["legs"] = JsonNode.Parse(legs), ["expiryDate"] = "26NOV26" });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Level == "error" && l.Message.Contains(words));
    }

    [Fact]
    public async Task A_generated_strategy_cannot_carry_a_stop_price_and_says_so()
    {
        var (api, run, outcome) = await RunAsync(new() { ["strategy"] = "straddle", ["priceType"] = "SL", ["triggerPrice"] = 5, ["price"] = 1, ["expiryDate"] = "26NOV26" });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Message.Contains("generated option strategies support only MARKET and LIMIT"));
    }

    [Fact]
    public async Task An_unknown_strategy_name_places_nothing()
    {
        var (api, run, outcome) = await RunAsync(new() { ["strategy"] = "butterfly", ["expiryDate"] = "26NOV26" });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Level == "warning" && l.Message.Contains("Unknown strategy type: butterfly"));
        Assert.Contains(run.Logs, l => l.Message.Contains("No legs generated for strategy: butterfly"));
    }
}


public sealed class FlowBasketOrderNodeTests
{
    private sealed class Api : IFlowApi
    {
        public List<(string Api, JsonObject Body)> Calls { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Calls.Add((api, (JsonObject)body.DeepClone()));
            return Task.FromResult(new JsonObject { ["status"] = "success" });
        }
    }

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - EPTradeBot.Core.Time.Ist.Offset;

    private static async Task<(Api Api, FlowRun Run, FlowOutcome Outcome)> RunAsync(JsonObject data, JsonObject? vars = null)
    {
        var api = new Api();
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        foreach (var (k, v) in vars ?? []) run.Context.Set(k, v?.DeepClone());
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph([new("s", "start", []), new("n", "basketOrder", data)], [new("s", "n", "")]), run, default);
        return (api, run, outcome);
    }

    [Fact]
    public async Task Lines_of_text_make_orders_that_take_each_exchanges_default_product()
    {
        var (api, _, outcome) = await RunAsync(new() { ["orders"] = "sbin, NSE, buy, 10\n\nNIFTY26NOV2624000CE,nfo,SELL,75", ["basketName"] = "morning" });
        Assert.True(outcome.Succeeded, outcome.Message);
        var (name, body) = Assert.Single(api.Calls);
        Assert.Equal("basketorder", name);
        Assert.Equal("""{"strategy":"morning","orders":[{"symbol":"sbin","exchange":"NSE","action":"BUY","quantity":10,"product":"MIS","pricetype":"MARKET","price":0,"trigger_price":0},{"symbol":"NIFTY26NOV2624000CE","exchange":"NFO","action":"SELL","quantity":75,"product":"NRML","pricetype":"MARKET","price":0,"trigger_price":0}]}""", body.ToJsonString());
    }

    [Fact]
    public async Task A_list_of_objects_can_set_its_own_price_type_and_fall_back_to_the_nodes()
    {
        var orders = JsonNode.Parse("""[{"symbol":"SBIN","exchange":"NSE","action":"BUY","quantity":"5","pricetype":"LIMIT","price":"{{p}}"},{"symbol":"INFY","exchange":"NSE","action":"SELL","quantity":1}]""")!;
        var (api, _, outcome) = await RunAsync(new() { ["orders"] = orders, ["product"] = "cnc", ["priceType"] = "market" }, new() { ["p"] = 812.5 });
        Assert.True(outcome.Succeeded, outcome.Message);
        var rows = api.Calls.Single().Body["orders"]!.AsArray();
        Assert.Equal(("LIMIT", 812.5, "CNC"), (rows[0]!["pricetype"]!.GetValue<string>(), rows[0]!["price"]!.GetValue<double>(), rows[0]!["product"]!.GetValue<string>()));
        Assert.Equal(("MARKET", 0.0, "CNC"), (rows[1]!["pricetype"]!.GetValue<string>(), rows[1]!["price"]!.GetValue<double>(), rows[1]!["product"]!.GetValue<string>()));
        Assert.Equal("flow_basket", api.Calls.Single().Body["strategy"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_orders_can_come_whole_from_a_variable()
    {
        var plan = new JsonObject { ["rows"] = JsonNode.Parse("""[{"symbol":"SBIN","exchange":"NSE","action":"BUY","quantity":3}]""") };
        var (api, _, outcome) = await RunAsync(new() { ["orders"] = "{{plan.rows}}" }, new() { ["plan"] = plan });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Single(api.Calls.Single().Body["orders"]!.AsArray());
    }

    [Theory]
    [InlineData("sbin,NSE,buy", "Basket order row 1: expected SYMBOL,EXCHANGE,ACTION,QTY")]
    [InlineData("sbin,NSE,buy,10\nx,NSE,hold,1", "Basket order row 2: invalid action 'HOLD'")]
    [InlineData("sbin,NSE,buy,0", "Basket order row 1: quantity must be positive, got 0")]
    [InlineData("sbin,XYZ,buy,1", "invalid exchange 'XYZ'")]
    [InlineData("sbin,NSE,buy,lots", "quantity must be numeric, got 'lots'")]
    [InlineData("", "No valid orders to place")]
    public async Task A_bad_row_stops_the_whole_basket_naming_the_row(string orders, string words)
    {
        var (api, run, outcome) = await RunAsync(new() { ["orders"] = orders });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Level == "error" && l.Message.Contains(words));
    }

    [Theory]
    [InlineData("""[5]""", "Basket order row 1: must be an order object")]
    [InlineData("""[{"symbol":"SBIN","exchange":"NSE","action":"BUY","quantity":"{{gone}}"}]""", "quantity has unresolved value")]
    [InlineData("""[{"symbol":"SBIN","exchange":"NSE","action":"BUY","quantity":true}]""", "quantity must be numeric, got boolean True")]
    [InlineData("""[{"symbol":"SBIN","exchange":"NSE","action":"BUY","quantity":1,"pricetype":"LIMIT"}]""", "LIMIT order needs a positive price")]
    public async Task A_bad_object_row_stops_the_basket(string orders, string words)
    {
        var (api, run, outcome) = await RunAsync(new() { ["orders"] = JsonNode.Parse(orders) });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Level == "error" && l.Message.Contains(words));
    }

    [Fact]
    public async Task A_bad_common_setting_is_reported_against_row_one()
    {
        var (api, run, outcome) = await RunAsync(new() { ["orders"] = "sbin,NSE,buy,1", ["product"] = "XX" });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Calls);
        Assert.Contains(run.Logs, l => l.Message.Contains("Basket order row 1: invalid product 'XX'"));
    }
}

public sealed class FlowHistoryNodeTests
{
    private sealed class Api(JsonObject? history = null, JsonObject? holidays = null) : IFlowApi
    {
        public List<(string Api, JsonObject Body)> Calls { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Calls.Add((api, (JsonObject)body.DeepClone()));
            return Task.FromResult(api == "holidays" ? (holidays ?? new JsonObject { ["status"] = "success", ["data"] = new JsonArray() }) : history is null ? new JsonObject { ["status"] = "success" } : (JsonObject)history.DeepClone());
        }
    }

    // Wednesday 11 November 2026, 12:00 India time.
    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - EPTradeBot.Core.Time.Ist.Offset;

    private static long Epoch(int month, int day, int hour = 9, int minute = 15) => new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(5.5)).ToUnixTimeSeconds();

    private static JsonObject Bar(long ts, double o, double h, double l, double c, double v = 100) => new() { ["timestamp"] = ts, ["open"] = o, ["high"] = h, ["low"] = l, ["close"] = c, ["volume"] = v };

    private static JsonObject Candles(params JsonObject[] rows) => new() { ["status"] = "success", ["data"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray()) };

    private static async Task<(Api Api, FlowRun Run, FlowOutcome Outcome)> RunAsync(string type, JsonObject data, Api api)
    {
        data["outputVariable"] = "out";
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph([new("s", "start", []), new("n", type, data)], [new("s", "n", "")]), run, default);
        return (api, run, outcome);
    }

    [Fact]
    public async Task History_with_days_counts_back_from_today_in_india_and_is_then_held_to_two_hundred_bars()
    {
        var (api, _, outcome) = await RunAsync("history", new() { ["symbol"] = "SBIN", ["days"] = 30, ["interval"] = "D" }, new Api(Candles(Bar(Epoch(11, 10), 1, 2, 0.5, 1.5))));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("""{"symbol":"SBIN","exchange":"NSE","interval":"D","start_date":"2026-10-12","end_date":"2026-11-11"}""", api.Calls[0].Body.ToJsonString());
        // Thirty days of five-minute bars is far more than two hundred, so the start moves up to 9 calendar days back.
        (api, _, _) = await RunAsync("history", new() { ["symbol"] = "SBIN", ["days"] = 30 }, new Api(Candles(Bar(Epoch(11, 10), 1, 2, 0.5, 1.5))));
        Assert.Equal("2026-11-02", api.Calls[0].Body["start_date"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_explicit_range_wins_but_is_pulled_in_to_at_most_two_hundred_bars()
    {
        var (api, _, _) = await RunAsync("history", new() { ["symbol"] = "SBIN", ["interval"] = "D", ["startDate"] = "2020-01-01", ["endDate"] = "2026-11-10", ["days"] = 5 }, new Api(Candles(Bar(Epoch(11, 10), 1, 2, 0.5, 1.5))));
        // 200 daily bars cover 325 calendar days (200 x 1.6 + 5) back from the end date.
        Assert.Equal("2025-12-20", api.Calls[0].Body["start_date"]!.GetValue<string>());
        Assert.Equal("2026-11-10", api.Calls[0].Body["end_date"]!.GetValue<string>());
        (api, _, _) = await RunAsync("history", new() { ["symbol"] = "SBIN", ["interval"] = "D", ["startDate"] = "2026-10-01", ["endDate"] = "2026-11-10" }, new Api(Candles(Bar(Epoch(11, 10), 1, 2, 0.5, 1.5))));
        Assert.Equal("2026-10-01", api.Calls[0].Body["start_date"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_long_answer_is_cut_to_the_latest_two_hundred_bars()
    {
        var rows = Enumerable.Range(0, 250).Select(i => Bar(1_700_000_000 + i * 300, i, i, i, i)).ToArray();
        var (_, run, _) = await RunAsync("history", new() { ["symbol"] = "SBIN" }, new Api(Candles(rows)));
        var stored = run.Context.Get("out")!["data"]!.AsArray();
        Assert.Equal(200, stored.Count);
        Assert.Equal(50, stored[0]!["open"]!.GetValue<double>());
    }

    [Fact]
    public async Task Bar_offset_zero_is_the_latest_closed_bar_and_the_one_still_forming_is_skipped()
    {
        var bars = new[] { Bar(Epoch(11, 11, 11, 30), 10, 11, 9, 10.5), Bar(Epoch(11, 11, 11, 35), 20, 21, 19, 20.5), Bar(Epoch(11, 11, 11, 55), 30, 31, 29, 30.5) };
        // 12:00 now: the 11:55 five-minute bar started five minutes ago, so it has just closed; at 11:58 it would still be forming.
        var (_, run, outcome) = await RunAsync("barOffset", new() { ["symbol"] = "SBIN", ["interval"] = "5m", ["offsetBars"] = 1 }, new Api(Candles(bars)));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(20.5, run.Context.Get("out")!["close"]!.GetValue<double>());
        Assert.Equal(1, run.Context.Get("out")!["offsetBars"]!.GetValue<long>());

        var forming = new[] { Bar(Epoch(11, 11, 11, 30), 10, 11, 9, 10.5), Bar(Epoch(11, 11, 11, 58), 30, 31, 29, 30.5) };
        (_, run, _) = await RunAsync("barOffset", new() { ["symbol"] = "SBIN", ["interval"] = "5m" }, new Api(Candles(forming)));
        Assert.Equal(10.5, run.Context.Get("out")!["close"]!.GetValue<double>());
    }

    [Fact]
    public async Task Bar_offset_reaching_past_the_history_fails_saying_how_far()
    {
        var (_, run, outcome) = await RunAsync("barOffset", new() { ["symbol"] = "SBIN", ["interval"] = "5m", ["offsetBars"] = 7 }, new Api(Candles(Bar(Epoch(11, 11, 10, 0), 1, 2, 0.5, 1.5))));
        Assert.False(outcome.Succeeded);
        Assert.Contains("Not enough history to reach 7 bars back for SBIN", outcome.Message);
        _ = run;
    }

    [Fact]
    public async Task The_previous_day_is_the_last_daily_bar_that_is_not_today()
    {
        var bars = new[] { Bar(Epoch(11, 9, 0, 0), 1, 5, 0.5, 4), Bar(Epoch(11, 10, 0, 0), 4, 9, 3, 8), Bar(Epoch(11, 11, 0, 0), 8, 12, 7, 11) };
        var (_, run, outcome) = await RunAsync("priorPeriodOhlc", new() { ["symbol"] = "SBIN" }, new Api(Candles(bars)));
        Assert.True(outcome.Succeeded, outcome.Message);
        var o = run.Context.Get("out")!;
        Assert.Equal(("2026-11-10", 9.0, 3.0, 8.0), (o["date"]!.GetValue<string>(), o["pdh"]!.GetValue<double>(), o["pdl"]!.GetValue<double>(), o["pdc"]!.GetValue<double>()));
    }

    [Fact]
    public async Task The_previous_week_and_month_are_aggregated_from_the_days_of_the_last_completed_one()
    {
        var days = new[] { Bar(Epoch(11, 2, 0, 0), 100, 110, 95, 105, 10), Bar(Epoch(11, 4, 0, 0), 105, 120, 100, 118, 20), Bar(Epoch(11, 6, 0, 0), 118, 119, 90, 92, 30), Bar(Epoch(11, 9, 0, 0), 92, 99, 91, 98, 5) };
        var (_, run, _) = await RunAsync("priorPeriodOhlc", new() { ["symbol"] = "SBIN", ["period"] = "previous_week" }, new Api(Candles(days)));
        var week = run.Context.Get("out")!;
        Assert.Equal(("2026-11-02/2026-11-08", 100.0, 120.0, 90.0, 92.0, 60.0), (week["date"]!.GetValue<string>(), week["open"]!.GetValue<double>(), week["high"]!.GetValue<double>(), week["low"]!.GetValue<double>(), week["close"]!.GetValue<double>(), week["volume"]!.GetValue<double>()));

        var months = new[] { Bar(Epoch(10, 5, 0, 0), 50, 60, 40, 55, 1), Bar(Epoch(10, 30, 0, 0), 55, 80, 54, 70, 2), Bar(Epoch(11, 2, 0, 0), 70, 71, 69, 70, 3) };
        (_, run, _) = await RunAsync("priorPeriodOhlc", new() { ["symbol"] = "SBIN", ["period"] = "previous_month" }, new Api(Candles(months)));
        var month = run.Context.Get("out")!;
        Assert.Equal(("2026-10", 50.0, 80.0, 40.0, 70.0), (month["date"]!.GetValue<string>(), month["open"]!.GetValue<double>(), month["high"]!.GetValue<double>(), month["low"]!.GetValue<double>(), month["close"]!.GetValue<double>()));
    }

    [Theory]
    [InlineData("previous_day", "not enough history to determine previous_day")]
    [InlineData("previous_hour", "not enough history to determine previous_hour")]
    [InlineData("previous_week", "not enough history to determine previous_week")]
    [InlineData("last_year", "unknown period 'last_year'")]
    public async Task Without_a_closed_period_to_report_the_node_says_so_rather_than_passing_off_the_current_one(string period, string words)
    {
        var (_, _, outcome) = await RunAsync("priorPeriodOhlc", new() { ["symbol"] = "SBIN", ["period"] = period }, new Api(Candles(Bar(Epoch(11, 11, 12, 0), 1, 2, 0.5, 1.5))));
        Assert.False(outcome.Succeeded);
        Assert.Contains(words, outcome.Message);
    }

    [Fact]
    public async Task A_failed_history_fetch_names_the_symbol_interval_and_a_hint()
    {
        var (_, _, outcome) = await RunAsync("barOffset", new() { ["symbol"] = "SBIN", ["interval"] = "7m" }, new Api(new JsonObject { ["status"] = "error", ["message"] = "bad interval" }));
        Assert.False(outcome.Succeeded);
        Assert.Contains("History fetch failed for SBIN (7m): bad interval.", outcome.Message);
        Assert.Contains("Intervals node", outcome.Message);
    }

    [Fact]
    public async Task A_missing_symbol_is_refused()
    {
        var (api, _, outcome) = await RunAsync("priorPeriodOhlc", [], new Api());
        Assert.False(outcome.Succeeded);
        Assert.Contains("Symbol is required", outcome.Message);
        Assert.Empty(api.Calls);
    }

    private static JsonObject Holidays(params (string Date, string Type, string[] Closed)[] days) => new()
    {
        ["status"] = "success",
        ["data"] = new JsonArray(days.Select(d => (JsonNode)new JsonObject { ["date"] = d.Date, ["holiday_type"] = d.Type, ["closed_exchanges"] = new JsonArray(d.Closed.Select(c => (JsonNode)c).ToArray()) }).ToArray()),
    };

    [Fact]
    public async Task The_calendar_answers_new_week_month_and_quarter_from_the_first_trading_day_not_the_date()
    {
        // 2026-11-02 is a Monday that is a holiday, so the week's first trading day is Tuesday the 3rd.
        var holidays = Holidays(("2026-11-02", "TRADING_HOLIDAY", ["NSE"]), ("2026-11-08", "SPECIAL_SESSION", []), ("2026-11-20", "SETTLEMENT_HOLIDAY", ["NSE"]));
        var (_, run, outcome) = await RunAsync("calendar", new() { ["date"] = "2026-11-03" }, new Api(null, holidays));
        Assert.True(outcome.Succeeded, outcome.Message);
        var c = run.Context.Get("out")!;
        // The 1st is a Sunday and the 2nd a holiday, so the 3rd also starts the month.
        Assert.Equal((true, true, true, "Tuesday", "2026-11-03"), (c["is_new_week"]!.GetValue<bool>(), c["is_trading_day"]!.GetValue<bool>(), c["is_new_month"]!.GetValue<bool>(), c["weekday"]!.GetValue<string>(), c["first_trading_day_of_week"]!.GetValue<string>()));
        Assert.Equal("2026-11-03", c["first_trading_day_of_month"]!.GetValue<string>());
        Assert.Equal("2026-10-30", c["prev_trading_day"]!.GetValue<string>());

        (_, run, _) = await RunAsync("calendar", new() { ["date"] = "2026-11-08" }, new Api(null, holidays));
        var sunday = run.Context.Get("out")!;
        Assert.Equal((true, true, true), (sunday["is_trading_day"]!.GetValue<bool>(), sunday["is_weekend"]!.GetValue<bool>(), sunday["is_special_session"]!.GetValue<bool>()));

        (_, run, _) = await RunAsync("calendar", new() { ["date"] = "2026-11-20" }, new Api(null, holidays));
        Assert.True(run.Context.Get("out")!["is_trading_day"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_calendar_defaults_to_the_trading_session_date_and_refuses_a_bad_date()
    {
        var (api, run, _) = await RunAsync("calendar", [], new Api());
        Assert.Equal("2026-11-11", run.Context.Get("out")!["date"]!.GetValue<string>());
        Assert.Equal(3, api.Calls.Count(c => c.Api == "holidays"));
        var (_, _, outcome) = await RunAsync("calendar", new() { ["date"] = "next tuesday" }, new Api());
        Assert.False(outcome.Succeeded);
        Assert.Contains("Calendar date must be YYYY-MM-DD, got 'next tuesday'", outcome.Message);
    }

    [Fact]
    public async Task When_the_holiday_list_is_unavailable_only_weekends_are_closed()
    {
        var (_, run, _) = await RunAsync("calendar", new() { ["date"] = "2026-11-13" }, new Api(null, new JsonObject { ["status"] = "error", ["message"] = "down" }));
        var friday = run.Context.Get("out")!;
        Assert.Equal((true, "2026-11-16"), (friday["is_trading_day"]!.GetValue<bool>(), friday["next_trading_day"]!.GetValue<string>()));
    }
}

public sealed class FlowMarginNodeTests
{
    private sealed class Api : IFlowApi
    {
        public List<JsonObject> Bodies { get; } = [];

        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
        {
            Assert.Equal("margin", api);
            Bodies.Add((JsonObject)body.DeepClone());
            return Task.FromResult(new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["total_margin_required"] = 1000 } });
        }
    }

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - EPTradeBot.Core.Time.Ist.Offset;

    private static async Task<(Api Api, FlowRun Run, FlowOutcome Outcome)> RunAsync(JsonObject data, JsonObject? vars = null)
    {
        var api = new Api();
        var run = new FlowRun(() => Noon, host: new FlowHost(api, "t"));
        foreach (var (k, v) in vars ?? []) run.Context.Set(k, v?.DeepClone());
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph([new("s", "start", []), new("n", "margin", data)], [new("s", "n", "")]), run, default);
        return (api, run, outcome);
    }

    [Fact]
    public async Task A_single_position_is_priced_as_a_one_item_basket_with_exchange_defaults()
    {
        var (api, _, outcome) = await RunAsync(new() { ["symbol"] = "NIFTY26NOV2624000CE", ["exchange"] = "NFO", ["quantity"] = "75", ["price"] = 120.5, ["action"] = "sell" });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("""{"positions":[{"exchange":"NFO","symbol":"NIFTY26NOV2624000CE","action":"SELL","quantity":"75","product":"NRML","pricetype":"MARKET","price":"120.5"}]}""", api.Bodies.Single().ToJsonString());
    }

    [Fact]
    public async Task A_basket_is_checked_and_normalised_position_by_position()
    {
        var basket = """[{"symbol":"sbin","exchange":"nse","action":"buy","quantity":"{{q}}","product":"mis","pricetype":"limit","price":"812.50","triggerPrice":0}]""";
        var (api, _, outcome) = await RunAsync(new() { ["positionsJson"] = basket }, new() { ["q"] = 10 });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("""{"positions":[{"symbol":"SBIN","exchange":"NSE","action":"BUY","quantity":"10","product":"MIS","pricetype":"LIMIT","price":"812.5","trigger_price":"0"}]}""", api.Bodies.Single().ToJsonString());
    }

    [Theory]
    [InlineData("not json", "Margin positions is not valid JSON after variable interpolation")]
    [InlineData("[]", "Margin positions is an empty basket")]
    [InlineData("[1]", "the basket contains a non-object entry")]
    [InlineData("\"\"", "Margin positions is empty after variable interpolation")]
    [InlineData("{{missing}}", "Margin positions has unresolved value")]
    [InlineData("[{\"symbol\":\"X\",\"exchange\":\"NSE\",\"action\":\"BUY\",\"quantity\":0,\"product\":\"MIS\",\"pricetype\":\"MARKET\"}]", "Margin position 1: quantity must be at least 1")]
    [InlineData("[{\"symbol\":\"X\",\"exchange\":\"NSE\",\"action\":\"BUY\",\"quantity\":1,\"product\":\"MIS\"}]", "Margin position 1: pricetype is required")]
    public async Task A_basket_that_cannot_be_used_is_refused_and_never_replaced_by_the_single_position(string basket, string words)
    {
        var (api, run, outcome) = await RunAsync(new() { ["positionsJson"] = basket == "\"\"" ? "" : basket, ["symbol"] = "SBIN" });
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Bodies);
        Assert.Contains(run.Logs, l => l.Level == "error" && l.Message.Contains(words));
    }

    [Fact]
    public async Task With_nothing_to_price_the_node_says_so()
    {
        var (api, run, outcome) = await RunAsync([]);
        Assert.False(outcome.Succeeded);
        Assert.Empty(api.Bodies);
        Assert.Contains(run.Logs, l => l.Message.Contains("Margin node has no positions and no symbol to price."));
    }
}

public sealed class FlowHttpRequestNodeTests
{
    private sealed class Http(string? rejection = null, FlowHttpResponse? reply = null, FlowHttpException? failure = null) : IFlowHttp
    {
        public List<(string Method, string Url, Dictionary<string, string> Headers, string? Json, string? Text, TimeSpan Timeout)> Sent { get; } = [];

        public Task<string?> CheckDestinationAsync(string url, CancellationToken ct) => Task.FromResult(rejection);

        public Task<FlowHttpResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, JsonNode? jsonBody, string? textBody, TimeSpan timeout, CancellationToken ct)
        {
            Sent.Add((method, url, new Dictionary<string, string>(headers), jsonBody?.ToJsonString(), textBody, timeout));
            return failure is null ? Task.FromResult(reply ?? new FlowHttpResponse(200, true, """{"ok":true}""")) : Task.FromException<FlowHttpResponse>(failure);
        }
    }

    private sealed class NoApi : IFlowApi
    {
        public Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct) => Task.FromResult(new JsonObject { ["status"] = "success" });
    }

    private static readonly DateTime Noon = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) - EPTradeBot.Core.Time.Ist.Offset;

    private static async Task<(Http Http, FlowRun Run, FlowOutcome Outcome)> RunAsync(JsonObject data, Http? http = null, JsonObject? vars = null)
    {
        http ??= new Http();
        data["outputVariable"] = "res";
        var run = new FlowRun(() => Noon, host: new FlowHost(new NoApi(), "t") { Http = http });
        foreach (var (k, v) in vars ?? []) run.Context.Set(k, v?.DeepClone());
        var outcome = await new FlowRunner(CoreFlowNodes.All().Concat(ApiFlowNodes.All())).RunAsync(new FlowGraph([new("s", "start", []), new("n", "httpRequest", data)], [new("s", "n", "")]), run, default);
        return (http, run, outcome);
    }

    [Fact]
    public async Task A_get_returns_the_status_and_parsed_body_and_keeps_them_in_the_variable()
    {
        var (http, run, outcome) = await RunAsync(new() { ["url"] = "https://example.com/report/{{id}}?key=SECRET", ["headers"] = """{"X-Token":"{{token}}"}""", ["timeout"] = 5000 }, null, new() { ["id"] = 7, ["token"] = "abc" });
        Assert.True(outcome.Succeeded, outcome.Message);
        var sent = Assert.Single(http.Sent);
        Assert.Equal(("GET", "https://example.com/report/7?key=SECRET", "abc", TimeSpan.FromSeconds(5)), (sent.Method, sent.Url, sent.Headers["X-Token"], sent.Timeout));
        Assert.True(run.Context.Get("res")!["data"]!["ok"]!.GetValue<bool>());
        Assert.Equal(200, run.Context.Get("res")!["statusCode"]!.GetValue<int>());
        Assert.Contains(run.Logs, l => l.Message == "HTTP GET https://example.com/report/7?<redacted>");
        Assert.DoesNotContain(run.Logs, l => l.Message.Contains("SECRET"));
    }

    [Fact]
    public async Task A_post_body_that_is_json_goes_as_json_and_anything_else_as_text()
    {
        var (http, _, _) = await RunAsync(new() { ["method"] = "post", ["url"] = "https://example.com", ["body"] = """{"qty":{{q}}}""" }, null, new() { ["q"] = 5 });
        Assert.Equal(("POST", """{"qty":5}""", null), (http.Sent[0].Method, http.Sent[0].Json, http.Sent[0].Text));
        (http, _, _) = await RunAsync(new() { ["method"] = "PUT", ["url"] = "https://example.com", ["body"] = "plain words" });
        Assert.Equal((null, "plain words"), (http.Sent[0].Json, http.Sent[0].Text));
        (http, _, _) = await RunAsync(new() { ["method"] = "PATCH", ["url"] = "https://example.com" });
        Assert.Equal("{}", http.Sent[0].Json);
    }

    [Fact]
    public async Task A_substituted_value_cannot_add_a_header_or_pull_a_second_reference_into_the_address()
    {
        var (http, _, _) = await RunAsync(new() { ["url"] = "https://example.com/{{path}}", ["headers"] = """{"X-A":"{{v}}"}""" }, null,
            new() { ["path"] = "{{secret}}", ["v"] = "x\", \"X-Injected\": \"yes", ["secret"] = "TOP" });
        Assert.Equal("https://example.com/{{secret}}", http.Sent[0].Url);
        Assert.Equal(["X-A"], http.Sent[0].Headers.Keys.ToArray());
        Assert.Equal("x\", \"X-Injected\": \"yes", http.Sent[0].Headers["X-A"]);
    }

    [Fact]
    public async Task A_rejected_destination_stops_before_anything_is_sent()
    {
        var (http, run, outcome) = await RunAsync(new() { ["url"] = "http://169.254.169.254/latest/meta-data" }, new Http("host '169.254.169.254' resolves to 169.254.169.254, which is not a public address"));
        Assert.False(outcome.Succeeded);
        Assert.Empty(http.Sent);
        Assert.Contains(run.Logs, l => l.Message.Contains("refusing to send this request: host '169.254.169.254'"));
    }

    [Theory]
    [InlineData("", "No URL specified")]
    [InlineData("TRACE", "Unsupported method: TRACE")]
    public async Task A_missing_address_or_an_unknown_method_is_refused(string value, string words)
    {
        var data = value == "" ? new JsonObject() : new JsonObject { ["url"] = "https://example.com", ["method"] = value };
        var (http, _, outcome) = await RunAsync(data);
        Assert.False(outcome.Succeeded);
        Assert.Contains(words, outcome.Message);
        Assert.Empty(http.Sent);
    }

    [Theory]
    [InlineData("{not json", "headers is not valid JSON")]
    [InlineData("[1]", "headers must be a JSON object, got list")]
    public async Task Bad_headers_stop_the_node(string headers, string words)
    {
        var (http, run, outcome) = await RunAsync(new() { ["url"] = "https://example.com", ["headers"] = headers });
        Assert.False(outcome.Succeeded);
        Assert.Empty(http.Sent);
        Assert.Contains(run.Logs, l => l.Level == "error" && l.Message.Contains(words));
    }

    [Fact]
    public async Task The_wait_is_read_in_milliseconds_capped_and_defaulted()
    {
        var (http, _, _) = await RunAsync(new() { ["url"] = "https://example.com", ["timeout"] = 600_000 });
        Assert.Equal(TimeSpan.FromSeconds(60), http.Sent[0].Timeout);
        (http, _, _) = await RunAsync(new() { ["url"] = "https://example.com", ["timeout"] = 0 });
        Assert.Equal(TimeSpan.FromSeconds(30), http.Sent[0].Timeout);
    }

    [Fact]
    public async Task A_non_success_status_is_kept_as_an_error_result_with_its_code_and_a_connection_failure_is_not()
    {
        var (_, run, outcome) = await RunAsync(new() { ["url"] = "https://example.com" }, new Http(null, new FlowHttpResponse(404, false, "missing")));
        Assert.False(outcome.Succeeded);
        Assert.Equal((404, "missing"), (run.Context.Get("res")!["statusCode"]!.GetValue<int>(), run.Context.Get("res")!["data"]!.GetValue<string>()));
        (_, run, outcome) = await RunAsync(new() { ["url"] = "https://example.com" }, new Http(null, null, new FlowHttpException("connection refused")));
        Assert.False(outcome.Succeeded);
        Assert.Contains("connection refused", outcome.Message);
        Assert.Null(run.Context.Get("res"));
    }
}
