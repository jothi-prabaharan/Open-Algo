using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

/// <summary>What the nodes that call the platform's API have in common.</summary>
internal static class ApiNodeSupport
{
    public static FlowHost HostOf(FlowRun run) => run.Host as FlowHost ?? throw new InvalidOperationException("This workflow run has no way to reach the platform's API.");

    /// <summary>Keeps a node's answer in the variable its author named, so later nodes can read it as <c>{{name.data.ltp}}</c>.</summary>
    public static void Store(FlowNode node, FlowRun run, JsonObject result)
    {
        if (node.Data["outputVariable"] is JsonValue v && v.TryGetValue<string>(out var name) && name.Trim().Length > 0)
        {
            run.Context.Set(name.Trim(), result.DeepClone());
            run.Log($"Stored result in variable: {name}");
        }
    }

    public static string Tag(FlowNode node, FlowRun run, FlowHost host)
    {
        var tag = node.Data.Str(run.Context, "strategyTag", "");
        return tag.Length > 0 ? tag : host.StrategyTag;
    }

    /// <summary>The first field the author filled in, with its references substituted; null when none was.</summary>
    public static string? Supplied(FlowNode node, FlowRun run, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!node.Data.ContainsKey(key)) continue;
            var raw = node.Data[key] is JsonValue v && !(v.TryGetValue<bool>(out var b) && !b) && !(FlowData.TryNumber(v, out var n) && n == 0) ? FlowContext.PyStr(v) : "";
            var text = run.Context.Interpolate(raw).Trim();
            if (text.Length > 0) return text;
        }

        return null;
    }

    public static string Level(JsonObject result) => FlowApiResults.IsSuccess(result) ? "info" : "error";

    public static FlowNodeResult RuntimeOrderError(FlowRun run, string label, ArgumentException error)
    {
        var message = $"{label} has invalid runtime order data: {error.Message}";
        run.Log(message, "error");
        return FlowNodeResult.Fail(message);
    }

    public static JsonObject Standard(StandardOrder o) => new()
    {
        ["symbol"] = o.Symbol, ["exchange"] = o.Exchange, ["action"] = o.Action, ["quantity"] = o.Quantity, ["pricetype"] = o.PriceType,
        ["product"] = o.Product, ["price"] = o.Price, ["trigger_price"] = o.TriggerPrice,
    };
}

public sealed record StandardOrder(string Symbol, string Exchange, string Action, long Quantity, string PriceType, string Product, double Price, double TriggerPrice);

/// <summary>Place order and smart order: the fields shared by the regular and the position-sized order.</summary>
public sealed class PlaceOrderNodes : IFlowNode
{
    public IReadOnlyList<string> Types => ["placeOrder", "smartOrder"];

    internal static StandardOrder Resolve(FlowNode node, FlowRun run, bool allowZeroQuantity)
    {
        var v = new RuntimeOrderResolver(run.Context, node.Data);
        // The symbol is read first, so a node with several problems reports the same one it always did. Upper case: an alert does not control its own
        // casing, and every symbol here is upper case.
        var symbol = v.Text("symbol").ToUpperInvariant();
        var exchange = v.Enum("exchange", OrderVocabulary.Exchanges, "NSE");
        var order = new StandardOrder(
            symbol, exchange,
            v.Enum("action", OrderVocabulary.Actions, "BUY"),
            v.Integer("quantity", 1, allowZeroQuantity ? 0 : 1),
            v.Enum("priceType", OrderVocabulary.PriceTypes, "MARKET"),
            v.Enum("product", OrderVocabulary.Products, OrderVocabulary.DefaultProduct(exchange)),
            v.Number("price", 0.0),
            v.Number("triggerPrice", 0.0));
        if (OrderVocabulary.InvalidPriceReason(order.PriceType, order.Price, order.TriggerPrice) is { } invalid) throw new ArgumentException(invalid);
        return order;
    }

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var smart = node.Type == "smartOrder";
        var label = smart ? "Smart order" : "Place order";
        JsonObject body;
        try
        {
            var order = Resolve(node, run, allowZeroQuantity: smart);
            body = ApiNodeSupport.Standard(order);
            if (smart) body["position_size"] = new RuntimeOrderResolver(run.Context, node.Data).Integer("positionSize", 0);
            run.Log(smart ? $"Placing smart order: {order.Symbol} {order.Action}" : $"Placing order: {order.Symbol} {order.Action} qty={order.Quantity}");
        }
        catch (ArgumentException ex)
        {
            return ApiNodeSupport.RuntimeOrderError(run, label, ex);
        }

        body["strategy"] = ApiNodeSupport.Tag(node, run, host);
        var result = await host.Api.CallAsync(smart ? "placesmartorder" : "placeorder", body, ct);
        run.Log($"{(smart ? "Smart order" : "Order")} result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }
}

public sealed class ModifyOrderNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["modifyOrder"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        FlowNodeResult Abort(string message)
        {
            run.Log($"Modify order aborted: {message}", "error");
            return FlowNodeResult.Fail(message);
        }

        var orderId = node.Data.Str(run.Context, "orderId", "");
        if (orderId.Length == 0) return Abort("orderId is required to modify an order");

        // Everything but the changed fields is read back from the live order, so an unspecified field keeps its current value, which is what "modify"
        // means. Guessing them would flip a live SELL to BUY or a 500-lot order to 1 lot on a broker that accepts the field unchecked.
        var existing = await host.Api.CallAsync("orderstatus", new JsonObject { ["orderid"] = orderId }, ct);
        if (!FlowApiResults.IsSuccess(existing) || existing["data"] is not JsonObject live)
            return Abort($"Could not read order {orderId} to modify it: {FlowApiResults.Reason(existing, "order not found")}");

        string Live(string key) => live[key] is JsonValue v ? FlowContext.PyStr(v) : "";
        var symbol = ApiNodeSupport.Supplied(node, run, "symbol") ?? Live("symbol");
        var exchange = ApiNodeSupport.Supplied(node, run, "exchange") ?? Live("exchange");
        var action = (ApiNodeSupport.Supplied(node, run, "action") ?? Live("action")).ToUpperInvariant();
        var product = ApiNodeSupport.Supplied(node, run, "product") ?? Live("product");
        var priceType = (ApiNodeSupport.Supplied(node, run, "priceType", "pricetype") ?? Live("pricetype")).ToUpperInvariant();
        var missing = new[] { ("symbol", symbol), ("exchange", exchange), ("action", action), ("product", product), ("priceType", priceType) }.Where(x => x.Item2.Length == 0).Select(x => x.Item1).ToList();
        if (missing.Count > 0) return Abort($"Order {orderId} did not report {string.Join(", ", missing)}; refusing to modify it with a guessed value");

        double quantity, price, trigger;
        try
        {
            double Pick(string? supplied, string liveKey) => supplied is { } s ? FlowData.ToDouble(JsonValue.Create(s)) : live[liveKey] is { } l && !(l is JsonValue lv && FlowData.TryNumber(lv, out var z) && z == 0) ? FlowData.ToDouble(l) : 0;
            quantity = Math.Truncate(Pick(ApiNodeSupport.Supplied(node, run, "newQuantity", "quantity"), "quantity"));
            price = Pick(ApiNodeSupport.Supplied(node, run, "newPrice", "price"), "price");
            trigger = Pick(ApiNodeSupport.Supplied(node, run, "newTriggerPrice", "triggerPrice"), "trigger_price");
        }
        catch (FormatException ex)
        {
            return Abort($"Modify order got a non-numeric price or quantity: {ex.Message}");
        }

        if (quantity <= 0) return Abort($"Modify order needs a positive quantity, got {(long)quantity}");

        run.Log($"Modifying order: {orderId} - {symbol} {action} qty={(long)quantity} price={FlowContext.PyFloat(price)} trigger={FlowContext.PyFloat(trigger)}");
        var result = await host.Api.CallAsync("modifyorder", new JsonObject
        {
            ["orderid"] = orderId, ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = action, ["quantity"] = (long)quantity, ["pricetype"] = priceType,
            ["product"] = product, ["price"] = price, ["trigger_price"] = trigger, ["disclosed_quantity"] = 0, ["strategy"] = ApiNodeSupport.Tag(node, run, host),
        }, ct);
        run.Log($"Modify order result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }
}

public sealed class CancelOrderNodes : IFlowNode
{
    public IReadOnlyList<string> Types => ["cancelOrder", "cancelAllOrders"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        JsonObject result;
        if (node.Type == "cancelOrder")
        {
            var id = run.Context.Interpolate(node.Data["orderId"] is JsonValue v ? FlowContext.PyStr(v) : "");
            run.Log($"Cancelling order: {id}");
            result = await host.Api.CallAsync("cancelorder", new JsonObject { ["orderid"] = id, ["strategy"] = ApiNodeSupport.Tag(node, run, host) }, ct);
            run.Log($"Cancel result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        }
        else
        {
            run.Log("Cancelling all orders");
            result = await host.Api.CallAsync("cancelallorder", new JsonObject { ["strategy"] = ApiNodeSupport.Tag(node, run, host) }, ct);
            run.Log($"Cancel all result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
            ApiNodeSupport.Store(node, run, result);
        }

        return FlowApiResults.ToResult(result);
    }
}

/// <summary>Close positions: everything, or one symbol when the node names one (squaring off the position it names and nothing else).</summary>
public sealed class ClosePositionsNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["closePositions"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var tag = ApiNodeSupport.Tag(node, run, host);
        var symbol = ApiNodeSupport.Supplied(node, run, "symbol");
        JsonObject result;
        if (symbol is null)
        {
            run.Log("Closing all positions");
            result = await host.Api.CallAsync("closeposition", new JsonObject { ["strategy"] = tag }, ct);
            run.Log($"Close all positions result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
            ApiNodeSupport.Store(node, run, result);
            return FlowApiResults.ToResult(result);
        }

        var exchange = ApiNodeSupport.Supplied(node, run, "exchange") ?? "NSE";
        var product = ApiNodeSupport.Supplied(node, run, "product") ?? OrderVocabulary.DefaultProduct(exchange);
        run.Log($"Closing position: {symbol}@{exchange} ({product})");
        var book = await host.Api.CallAsync("positionbook", [], ct);
        if (!FlowApiResults.IsSuccess(book)) return Finish(node, run, book);
        var held = (book["data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p =>
            Text(p, "symbol") == symbol && Text(p, "exchange") == exchange && Text(p, "product") == product && Qty(p) != 0);
        if (held is null)
        {
            result = new JsonObject { ["status"] = "success", ["message"] = $"No open position in {symbol} to close" };
            run.Log($"Close position result: {result.ToJsonString()}");
            ApiNodeSupport.Store(node, run, result);
            return FlowApiResults.ToResult(result);
        }

        var quantity = Qty(held);
        result = await host.Api.CallAsync("placeorder", new JsonObject
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = quantity > 0 ? "SELL" : "BUY", ["quantity"] = (long)Math.Abs(quantity),
            ["pricetype"] = "MARKET", ["product"] = product, ["price"] = 0.0, ["trigger_price"] = 0.0, ["strategy"] = tag,
        }, ct);
        return Finish(node, run, result);
    }

    private static FlowNodeResult Finish(FlowNode node, FlowRun run, JsonObject result)
    {
        run.Log($"Close position result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v ? FlowContext.PyStr(v) : "";

    private static double Qty(JsonObject o) => o["quantity"] is JsonValue v && FlowData.TryParseOrNumber(v, out var d) ? d : 0;
}

/// <summary>The data nodes that read the platform and keep the answer in a variable.</summary>
public sealed class DataNodes : IFlowNode
{
    public IReadOnlyList<string> Types => ["getQuote", "getDepth", "getOrderStatus", "openPosition", "orderBook", "tradeBook", "positionBook", "holdings", "funds"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        JsonObject result;
        switch (node.Type)
        {
            case "getQuote":
            case "getDepth":
            {
                var symbol = node.Data.Str(ctx, "symbol", "");
                var exchange = node.Data.Str(ctx, "exchange", "NSE");
                run.Log(node.Type == "getQuote" ? $"Getting quote for: {symbol}" : $"Getting depth for: {symbol}");
                result = await host.Api.CallAsync(node.Type == "getQuote" ? "quotes" : "depth", new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange }, ct);
                break;
            }

            case "getOrderStatus":
            {
                var id = node.Data.Str(ctx, "orderId", "");
                run.Log($"Getting order status for: {id}");
                result = await host.Api.CallAsync("orderstatus", new JsonObject { ["orderid"] = id }, ct);
                break;
            }

            case "openPosition":
            {
                var symbol = node.Data.Str(ctx, "symbol", "");
                var exchange = node.Data.Str(ctx, "exchange", "NSE");
                var product = node.Data.Str(ctx, "product", OrderVocabulary.DefaultProduct(exchange));
                run.Log($"Getting open position for: {symbol}");
                result = await OpenPositionAsync(host, symbol, exchange, product, ct);
                break;
            }

            default:
                var (api, label) = node.Type switch
                {
                    "orderBook" => ("orderbook", "order book"),
                    "tradeBook" => ("tradebook", "trade book"),
                    "positionBook" => ("positionbook", "position book"),
                    "holdings" => ("holdings", "holdings"),
                    _ => ("funds", "funds"),
                };
                run.Log($"Fetching {label}");
                result = await host.Api.CallAsync(api, [], ct);
                run.Log($"{char.ToUpperInvariant(label[0])}{label[1..]} received");
                break;
        }

        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }

    /// <summary>The quantity and profit of one position, read from the position book; zero when none matches. Port of the Flow client's get_open_position.</summary>
    public static async Task<JsonObject> OpenPositionAsync(FlowHost host, string symbol, string exchange, string? product, CancellationToken ct)
    {
        var book = await host.Api.CallAsync("positionbook", [], ct);
        if (!FlowApiResults.IsSuccess(book)) return book;
        foreach (var p in (book["data"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (Text(p, "symbol") != symbol || Text(p, "exchange") != exchange) continue;
            var held = p["product"] is not null ? Text(p, "product") : Text(p, "product_type");
            if (!string.IsNullOrEmpty(product) && held != product) continue;
            return new JsonObject { ["status"] = "success", ["quantity"] = p["quantity"]?.DeepClone() ?? 0, ["pnl"] = p["pnl"]?.DeepClone() ?? 0 };
        }

        return new JsonObject { ["status"] = "success", ["quantity"] = 0, ["pnl"] = 0 };
    }

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v ? FlowContext.PyStr(v) : "";
}

/// <summary>Price, position and funds conditions: each reads the platform and forks into a true and a false branch.</summary>
public sealed class CheckNodes : IFlowNode
{
    public static readonly IReadOnlyList<string> PriceFields = ["change_percent", "high", "low", "ltp", "open", "prev_close"];

    public IReadOnlyList<string> Types => ["priceCondition", "positionCheck", "fundCheck"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct) => node.Type switch
    {
        "priceCondition" => PriceAsync(node, run, ct),
        "positionCheck" => PositionAsync(node, run, ct),
        _ => FundAsync(node, run, ct),
    };

    private static FlowNodeResult? Abort(FlowRun run, string what, string message)
    {
        run.Log($"{what} aborted: {message}", "error");
        return FlowNodeResult.Fail(message, false);
    }

    private static double Num(JsonNode? n) => n is null ? 0 : n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0 ? 0 : FlowData.ToDouble(n);

    private async Task<FlowNodeResult?> PriceAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        var symbol = node.Data.Str(ctx, "symbol", "");
        var exchange = node.Data.Str(ctx, "exchange", "NSE");
        var field = node.Data.Str(ctx, "field", "ltp");
        var op = node.Data.Str(ctx, "operator", ">");
        if (!PriceFields.Contains(field)) return Abort(run, "Price check", $"field '{field}' is not one of {string.Join(", ", PriceFields)}");
        if (!VarConditionNode.Operators.Contains(op)) return Abort(run, "Price check", $"operator '{op}' is not one of {string.Join(", ", VarConditionNode.Operators.Order(StringComparer.Ordinal))}");

        var raw = node.Data["value"] ?? node.Data["threshold"];
        double threshold;
        try
        {
            threshold = raw is null || raw is JsonValue rv && rv.TryGetValue<string>(out var rs) && rs.Length == 0 ? 0.0 : FlowData.ToDouble(JsonValue.Create(ctx.Interpolate(FlowContext.PyStr(raw))));
        }
        catch (FormatException)
        {
            return Abort(run, "Price check", $"value did not resolve to a number: {FlowContext.PyStr(raw)}");
        }

        run.Log($"Checking price for: {symbol}");
        var quote = await host.Api.CallAsync("quotes", new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange }, ct);
        // A failed read is not an answer: reading straight through would evaluate the condition on zeroes.
        if (!FlowApiResults.IsSuccess(quote)) return Abort(run, "Price check", FlowApiResults.Reason(quote, "the price lookup failed"));
        var data = quote["data"] as JsonObject ?? [];
        double fieldValue;
        if (field == "change_percent")
        {
            var ltp = Num(data["ltp"]);
            var prev = Num(data["prev_close"]);
            fieldValue = prev != 0 ? (ltp - prev) / prev * 100.0 : 0.0;
        }
        else fieldValue = Num(data[field]);

        var met = VarConditionNode.Compare(fieldValue, op, threshold);
        run.Log($"Price check: {field}={FlowContext.PyFloat(fieldValue)} {op} {FlowContext.PyFloat(threshold)} = {(met ? "True" : "False")}");
        return FlowNodeResult.Branch(met, new JsonObject { ["ltp"] = Num(data["ltp"]), ["field"] = field, ["field_value"] = fieldValue });
    }

    private async Task<FlowNodeResult?> PositionAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        var symbol = node.Data.Str(ctx, "symbol", "");
        var exchange = node.Data.Str(ctx, "exchange", "NSE");
        var product = node.Data.Str(ctx, "product", OrderVocabulary.DefaultProduct(exchange));
        var condition = node.Data.Str(ctx, "condition", "exists");
        var threshold = FloatOr(node, ctx, "threshold", 0.0);
        if (symbol.Length == 0)
            // Fail closed: an empty symbol reads a zero position, which would let whichever condition the author needs pass.
            return Abort(run, "Position check", "Position Check has no symbol set, so it cannot guard anything.");

        run.Log($"Checking position for: {symbol}");
        var result = await DataNodes.OpenPositionAsync(host, symbol, exchange, product, ct);
        if (!FlowApiResults.IsSuccess(result)) return Abort(run, "Position check", FlowApiResults.Reason(result, "the position lookup failed"));
        var quantity = (long)Num(result["quantity"]);
        var pnl = Num(result["pnl"]);
        bool met;
        string line;
        switch (condition)
        {
            case "exists": met = quantity != 0; line = $"qty={quantity} != 0"; break;
            case "not_exists": met = quantity == 0; line = $"qty={quantity} == 0"; break;
            case "quantity_above": met = quantity > threshold; line = $"qty={quantity} > {FlowContext.PyFloat(threshold)}"; break;
            case "quantity_below": met = quantity < threshold; line = $"qty={quantity} < {FlowContext.PyFloat(threshold)}"; break;
            case "pnl_above": met = pnl > threshold; line = $"pnl={FlowContext.PyFloat(pnl)} > {FlowContext.PyFloat(threshold)}"; break;
            case "pnl_below": met = pnl < threshold; line = $"pnl={FlowContext.PyFloat(pnl)} < {FlowContext.PyFloat(threshold)}"; break;
            default:
                return Abort(run, "Position check", $"condition '{condition}' is not one this node can evaluate (exists, not_exists, quantity_above, quantity_below, pnl_above, pnl_below)");
        }

        run.Log($"Position check: {line} = {(met ? "True" : "False")}");
        return FlowNodeResult.Branch(met, new JsonObject { ["quantity"] = quantity, ["pnl"] = pnl });
    }

    private async Task<FlowNodeResult?> FundAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        double minimum;
        if (node.Data.ContainsKey("minAvailable")) minimum = FloatOr(node, ctx, "minAvailable", 0.0);
        else if (node.Data.ContainsKey("threshold"))
        {
            // A saved-before pair of operator and threshold. This node only says "at least", so a less-than cannot be carried over: refuse, never invert the guard.
            var legacy = (node.Data["operator"] is JsonValue o ? FlowContext.PyStr(o) : "gt").Trim().ToLowerInvariant();
            if (legacy.Length == 0) legacy = "gt";
            if (!new[] { "gt", "gte", "ge", ">", ">=" }.Contains(legacy))
            {
                var message = $"Fund Check has a legacy '{legacy}' comparison, which this node cannot express - it only supports a minimum. Open the node and set Minimum Available.";
                run.Log(message, "error");
                return FlowNodeResult.Fail(message);
            }

            minimum = FloatOr(node, ctx, "threshold", 0.0);
            run.Log($"Fund Check is using its legacy threshold of {FlowContext.PyFloat(minimum)}. Re-save the node to store it as Minimum Available.", "warning");
        }
        else
        {
            // Fail closed: a default of zero would pass on any balance, a negative one included.
            const string message = "Fund Check has no Minimum Available set, so it cannot guard anything. Open the node and set a minimum.";
            run.Log(message, "error");
            return FlowNodeResult.Fail(message, false);
        }

        run.Log("Checking funds");
        var result = await host.Api.CallAsync("funds", [], ct);
        if (!FlowApiResults.IsSuccess(result)) return Abort(run, "Fund check", FlowApiResults.Reason(result, "the funds lookup failed"));
        var available = Num((result["data"] as JsonObject)?["availablecash"]);
        var met = available >= minimum;
        run.Log($"Fund check: available={FlowContext.PyFloat(available)} >= {FlowContext.PyFloat(minimum)} = {(met ? "True" : "False")}");
        return FlowNodeResult.Branch(met, new JsonObject { ["available"] = available });
    }

    /// <summary>A number field that may be written as text with references; the default when absent, empty or unreadable.</summary>
    private static double FloatOr(FlowNode node, FlowContext ctx, string key, double fallback)
    {
        var v = node.Data[key];
        if (v is null || v is JsonValue e && e.TryGetValue<string>(out var es) && es.Length == 0) return fallback;
        try
        {
            return v is JsonValue sv && sv.TryGetValue<string>(out var s) ? FlowData.ToDouble(JsonValue.Create(ctx.Interpolate(s))) : FlowData.ToDouble(v);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}

/// <summary>The node types that reach the platform's API.</summary>
public static class ApiFlowNodes
{
    public static IEnumerable<IFlowNode> All() => [new PlaceOrderNodes(), new IndicatorNode(), new ModifyOrderNode(), new CancelOrderNodes(), new ClosePositionsNode(), new DataNodes(), new CheckNodes(), new MarketInfoNodes(), new SplitOrderNode(), new OptionsOrderNode(), new OptionsMultiOrderNode(), new BasketOrderNode(), new HistoryNodes(), new MarginNode(), new HttpRequestNode()];
}

/// <summary>The nodes that look up instruments, expiries, option chains and the exchange calendar through the platform and keep the answer in a variable.</summary>
public sealed class MarketInfoNodes : IFlowNode
{
    public IReadOnlyList<string> Types => ["symbol", "optionSymbol", "expiry", "intervals", "multiQuotes", "optionChain", "syntheticFuture", "holidays", "timings"];

    /// <summary>An integer field: a number as given, text read as a number (and the default when it is not one), the default when empty.</summary>
    public static long Int(FlowNode node, FlowContext ctx, string key, long fallback)
    {
        var raw = node.Data[key];
        if (raw is null || raw is JsonValue e && e.TryGetValue<string>(out var empty) && empty.Length == 0) return fallback;
        if (raw is JsonValue v && v.TryGetValue<string>(out var text))
            return FlowData.TryParseFloat(ctx.Interpolate(text), out var d) ? (long)d : fallback;
        return raw is JsonValue n && FlowData.TryNumber(n, out var number) ? (long)number : fallback;
    }

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        string api;
        JsonObject body;
        switch (node.Type)
        {
            case "symbol":
            {
                var symbol = node.Data.Str(ctx, "symbol", "");
                var exchange = node.Data.Str(ctx, "exchange", "NSE");
                run.Log($"Getting symbol info for: {symbol} ({exchange})");
                (api, body) = ("symbol", new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange });
                break;
            }

            case "optionSymbol":
            {
                var underlying = node.Data.Str(ctx, "underlying", "NIFTY");
                var offset = node.Data.Str(ctx, "offset", "ATM");
                var type = node.Data.Str(ctx, "optionType", "CE");
                run.Log($"Resolving option symbol: {underlying} {type} {offset}");
                (api, body) = ("optionsymbol", new JsonObject
                {
                    ["underlying"] = underlying, ["exchange"] = node.Data.Str(ctx, "exchange", "NSE_INDEX"), ["expiry_date"] = node.Data.Str(ctx, "expiryDate", ""), ["offset"] = offset, ["option_type"] = type,
                });
                break;
            }

            case "expiry":
            {
                var symbol = node.Data.Str(ctx, "symbol", "NIFTY");
                var type = node.Data.Str(ctx, "instrumenttype", "options").ToLowerInvariant();
                if (type is not ("options" or "futures")) type = "options";
                run.Log($"Getting {type} expiry dates for: {symbol}");
                (api, body) = ("expiry", new JsonObject { ["symbol"] = symbol, ["exchange"] = node.Data.Str(ctx, "exchange", "NFO"), ["instrumenttype"] = type });
                break;
            }

            case "intervals":
                run.Log("Getting available intervals");
                (api, body) = ("intervals", []);
                break;

            case "multiQuotes":
            {
                var raw = node.Data.Str(ctx, "symbols", "");
                var exchange = node.Data.Str(ctx, "exchange", "NSE");
                var symbols = new JsonArray();
                foreach (var s in raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)) symbols.Add(new JsonObject { ["symbol"] = s, ["exchange"] = exchange });
                run.Log($"Getting quotes for {symbols.Count} symbols");
                (api, body) = ("multiquotes", new JsonObject { ["symbols"] = symbols });
                break;
            }

            case "optionChain":
            {
                var underlying = node.Data.Str(ctx, "underlying", "NIFTY");
                var expiry = node.Data.Str(ctx, "expiryDate", "");
                run.Log($"Fetching option chain for: {underlying} expiry={expiry}");
                (api, body) = ("optionchain", new JsonObject
                {
                    ["underlying"] = underlying, ["exchange"] = node.Data.Str(ctx, "exchange", "NSE_INDEX"), ["expiry_date"] = expiry, ["strike_count"] = Int(node, ctx, "strikeCount", 10),
                });
                break;
            }

            case "syntheticFuture":
            {
                var underlying = node.Data.Str(ctx, "underlying", "NIFTY");
                run.Log($"Calculating synthetic future for: {underlying}");
                (api, body) = ("syntheticfuture", new JsonObject { ["underlying"] = underlying, ["exchange"] = node.Data.Str(ctx, "exchange", "NSE_INDEX"), ["expiry_date"] = node.Data.Str(ctx, "expiryDate", "") });
                break;
            }

            case "holidays":
            {
                var year = Int(node, ctx, "year", 0);
                run.Log($"Fetching holidays for year: {(year == 0 ? "current" : year.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
                (api, body) = ("holidays", year == 0 ? [] : new JsonObject { ["year"] = year });
                break;
            }

            default:
            {
                var date = node.Data.Str(ctx, "date", "");
                if (date.Length == 0) date = (run.UtcNow() + EPTradeBot.Core.Time.Ist.Offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                run.Log($"Fetching market timings for date: {date}");
                (api, body) = ("timings", new JsonObject { ["date"] = date });
                break;
            }
        }

        var result = await host.Api.CallAsync(api, body, ct);
        run.Log($"{node.Type} result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }
}

/// <summary>Split order: one large order sent as a run of smaller ones of at most <c>splitSize</c> each.</summary>
public sealed class SplitOrderNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["splitOrder"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        JsonObject body;
        try
        {
            var order = PlaceOrderNodes.Resolve(node, run, allowZeroQuantity: false);
            var splitSize = new RuntimeOrderResolver(run.Context, node.Data).Integer("splitSize", 10, 1);
            body = ApiNodeSupport.Standard(order);
            body["splitsize"] = splitSize;
            run.Log($"Placing split order: {order.Symbol} qty={order.Quantity} split={splitSize}");
        }
        catch (ArgumentException ex)
        {
            return ApiNodeSupport.RuntimeOrderError(run, "Split order", ex);
        }

        body["strategy"] = ApiNodeSupport.Tag(node, run, host);
        var result = await host.Api.CallAsync("splitorder", body, ct);
        run.Log($"Split order result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }
}

/// <summary>Where an options underlying is quoted and where its options trade, and how an expiry is chosen. Port of the helpers OpenAlgo's two options nodes share.</summary>
public static class OptionVocabulary
{
    private static readonly string[] McxUnderlyings = ["GOLD", "GOLDM", "SILVER", "SILVERM", "CRUDEOIL", "CRUDEOILM", "NATURALGAS", "NATGASMINI", "COPPER", "ZINC", "MCXBULLDEX"];

    public static readonly IReadOnlyDictionary<string, (string Quote, string Option)> ByUnderlying = new Dictionary<string, (string, string)>
    {
        ["SENSEX"] = ("BSE_INDEX", "BFO"), ["BANKEX"] = ("BSE_INDEX", "BFO"), ["SENSEX50"] = ("BSE_INDEX", "BFO"),
    }.Concat(McxUnderlyings.Select(u => new KeyValuePair<string, (string, string)>(u, ("MCX", "MCX")))).ToDictionary(kv => kv.Key, kv => kv.Value);

    public static readonly IReadOnlyDictionary<string, (string Quote, string Option)> ByDeclared = new Dictionary<string, (string, string)>
    {
        ["BFO"] = ("BSE_INDEX", "BFO"), ["BSE"] = ("BSE_INDEX", "BFO"), ["BSE_INDEX"] = ("BSE_INDEX", "BFO"), ["NFO"] = ("NSE_INDEX", "NFO"), ["NSE"] = ("NSE_INDEX", "NFO"), ["NSE_INDEX"] = ("NSE_INDEX", "NFO"),
        ["MCX"] = ("MCX", "MCX"), ["CDS"] = ("CDS", "CDS"), ["BCD"] = ("BCD", "BCD"), ["NCDEX"] = ("NCDEX", "NCDEX"), ["NCO"] = ("NCO", "NCO"),
    };

    public static readonly IReadOnlyDictionary<string, int> FallbackLotSizes = new Dictionary<string, int>
    {
        ["NIFTY"] = 65, ["BANKNIFTY"] = 30, ["FINNIFTY"] = 60, ["MIDCPNIFTY"] = 120, ["NIFTYNXT50"] = 25, ["SENSEX"] = 20, ["BANKEX"] = 30, ["SENSEX50"] = 25,
    };

    public static readonly string[] ExpiryTypes = ["current_month", "current_week", "next_month", "next_week"];

    /// <summary>A named underlying decides on its own; the node's declared exchange only matters for one the table does not name.</summary>
    public static (string Quote, string Option) Exchanges(string underlying, string declared) =>
        ByUnderlying.TryGetValue(underlying.Trim().ToUpperInvariant(), out var named) ? named
        : ByDeclared.TryGetValue(declared.Trim().ToUpperInvariant(), out var d) ? d : ("NSE_INDEX", "NFO");

    private static DateOnly? ParseExpiry(string value)
    {
        var text = value.Replace("-", "").ToUpperInvariant();
        return DateTime.TryParseExact(text, "ddMMMyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>The expiry a relative type resolves to, as DDMMMYY: the nearest or second nearest for the weekly types, the last in the month for the monthly ones; null when it cannot be satisfied.</summary>
    public static string? SelectExpiry(IEnumerable<string> expiries, string type, DateOnly today)
    {
        var parsed = expiries.Select(e => (Value: e, When: ParseExpiry(e))).Where(p => p.When is not null).OrderBy(p => p.When).ToList();
        if (parsed.Count == 0) return null;
        static string Format(string v) => v.Replace("-", "").ToUpperInvariant();
        switch (type)
        {
            case "current_week": return Format(parsed[0].Value);
            case "next_week": return parsed.Count > 1 ? Format(parsed[1].Value) : null;
            case "current_month":
            case "next_month":
                var month = today.Month;
                var year = today.Year;
                if (type == "next_month") (month, year) = month == 12 ? (1, year + 1) : (month + 1, year);
                var matches = parsed.Where(p => p.When!.Value.Month == month && p.When.Value.Year == year).ToList();
                return matches.Count > 0 ? Format(matches[^1].Value) : null;
            default: return null;
        }
    }

    /// <summary>The lot size for an underlying, or the sentence saying why it cannot be known; it never guesses for an exchange that has contracts.</summary>
    public static async Task<(int? Lot, string? Error)> LotSizeAsync(FlowHost host, FlowRun run, string underlying, string foExchange, CancellationToken ct)
    {
        if (host.LotSizes is null) return (null, $"Could not read the lot size for {underlying} on {foExchange}. Refusing to size an order on a guess.");
        var answer = await host.LotSizes(underlying, foExchange, ct);
        switch (answer.Problem)
        {
            case LotSizeProblem.None when answer.Lot is { } lot: return (lot, null);
            case LotSizeProblem.NotListed:
                return (null, $"{underlying} has no contract on {foExchange} in the master contract. Check the symbol, or re-download the master contract.");
            case LotSizeProblem.ExchangeEmpty:
                if (!FallbackLotSizes.TryGetValue(underlying, out var fallback))
                    return (null, $"The master contract has no {foExchange} contracts loaded and no built-in lot size for {underlying}. Download the master contract first.");
                run.Log($"Master contract has no {foExchange} contracts loaded; using the built-in lot size {fallback} for {underlying}", "warning");
                return (fallback, null);
            default:
                return (null, $"Could not read the lot size for {underlying} on {foExchange}. Refusing to size an order on a guess.");
        }
    }
}

/// <summary>Options order: an option picked by offset from the money, sized in lots, on a weekly, monthly or named expiry.</summary>
public sealed class OptionsOrderNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["optionsOrder"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        string underlying, expiryType, expiryInput, offset, optionType, action, priceType, product;
        long lots, splitSize;
        double price, trigger;
        try
        {
            var v = new RuntimeOrderResolver(ctx, node.Data);
            underlying = v.Text("underlying", "NIFTY").ToUpperInvariant();
            expiryType = v.Text("expiryType", "current_week").ToLowerInvariant();
            expiryInput = v.OptionalText("expiryDate").ToUpperInvariant();
            if (expiryInput.Length == 0 && System.Text.RegularExpressions.Regex.IsMatch(expiryType.ToUpperInvariant(), Contracts.V1.ApiPatterns.ExpiryDdMmmYy)) expiryInput = expiryType.ToUpperInvariant();
            if (expiryInput.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(expiryInput, Contracts.V1.ApiPatterns.ExpiryDdMmmYy))
                throw new ArgumentException($"expiryDate must be in DDMMMYY format such as 28OCT25, got '{expiryInput}'");
            if (expiryInput.Length == 0 && !OptionVocabulary.ExpiryTypes.Contains(expiryType))
                throw new ArgumentException($"expiryType must be one of {string.Join(", ", OptionVocabulary.ExpiryTypes)}, got '{expiryType}'");
            lots = v.Integer("quantity", 1, 1);
            offset = v.Text("offset", "ATM").ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(offset, Contracts.V1.ApiPatterns.OptionOffset)) throw new ArgumentException($"offset must be ATM, ITM1-ITM50, or OTM1-OTM50, got '{offset}'");
            optionType = v.Enum("optionType", ["CE", "PE"], "CE");
            action = v.Enum("action", OrderVocabulary.Actions, "BUY");
            priceType = v.Enum("priceType", OrderVocabulary.PriceTypes, "MARKET");
            product = v.Enum("product", OrderVocabulary.Products, "NRML");
            splitSize = v.Integer("splitSize", 0, 0);
            price = v.Number("price", 0.0);
            trigger = v.Number("triggerPrice", 0.0);
            if (node.Data.ContainsKey("exchange")) v.Enum("exchange", OrderVocabulary.Exchanges);
            if (OrderVocabulary.InvalidPriceReason(priceType, price, trigger) is { } invalid) throw new ArgumentException(invalid);
        }
        catch (ArgumentException ex)
        {
            return ApiNodeSupport.RuntimeOrderError(run, "Options order", ex);
        }

        run.Log($"Placing options order: {underlying} {optionType} {offset}");
        var (underlyingExchange, foExchange) = OptionVocabulary.Exchanges(underlying, node.Data.Str(ctx, "exchange", ""));
        var (lot, lotError) = await OptionVocabulary.LotSizeAsync(host, run, underlying, foExchange, ct);
        if (lot is null)
        {
            run.Log($"Options order failed: {lotError}", "error");
            return FlowNodeResult.Fail(lotError!);
        }

        var total = lots * lot.Value;
        run.Log($"Lot size for {underlying}: {lot} -> {lots} lot(s) = {total}");
        var expiry = expiryInput;
        if (expiry.Length == 0)
        {
            var (expiryDate, expiryError) = await ResolveExpiryAsync(host, run, underlying, foExchange, expiryType, ct);
            if (expiryDate is null)
            {
                var message = $"Could not resolve expiry for {expiryType}";
                run.Log($"Options order failed: {message}", "error");
                return FlowNodeResult.Fail(expiryError ?? message);
            }

            expiry = expiryDate;
        }

        run.Log($"Resolved expiry: {(expiryInput.Length > 0 ? expiryInput : expiryType)} -> {expiry}");
        var result = await host.Api.CallAsync("optionsorder", new JsonObject
        {
            ["underlying"] = underlying, ["exchange"] = underlyingExchange, ["action"] = action, ["quantity"] = total, ["expiry_date"] = expiry, ["offset"] = offset, ["option_type"] = optionType,
            ["pricetype"] = priceType, ["product"] = product, ["price"] = price, ["trigger_price"] = trigger, ["splitsize"] = splitSize, ["strategy"] = ApiNodeSupport.Tag(node, run, host),
        }, ct);
        run.Log($"Options order result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }

    internal static async Task<(string? Date, string? Error)> ResolveExpiryAsync(FlowHost host, FlowRun run, string symbol, string exchange, string expiryType, CancellationToken ct)
    {
        try
        {
            var response = await host.Api.CallAsync("expiry", new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["instrumenttype"] = "options" }, ct);
            if (!FlowApiResults.IsSuccess(response)) { run.Log($"Failed to fetch expiry: {response.ToJsonString()}", "error"); return (null, null); }
            var list = (response["data"] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList() ?? [];
            if (list.Count == 0) { run.Log($"No expiry dates found for {symbol} on {exchange}", "error"); return (null, null); }
            var selected = OptionVocabulary.SelectExpiry(list, expiryType, DateOnly.FromDateTime(run.UtcNow() + EPTradeBot.Core.Time.Ist.Offset));
            if (selected is null) run.Log($"No expiry matches {expiryType} for {symbol} on {exchange} among {list.Count} listed contract(s)", "error");
            return (selected, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            run.Log($"Error resolving expiry: {e.Message}", "error");
            return (null, null);
        }
    }
}

/// <summary>Options multi-order: a straddle, strangle, condor or spread generated from a name, or custom legs each with its own strike or offset, expiry, side, size and price.</summary>
public sealed class OptionsMultiOrderNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["optionsMultiOrder"];

    private sealed record Leg(string? Offset, double? Strike, string ExpiryDate, string ExpiryType, string OptionType, string Action, long Lots, string PriceType, string Product, double Price, double Trigger, long SplitSize);

    private static JsonObject Failure(FlowRun run, string message)
    {
        run.Log($"Options multi-order failed: {message}", "error");
        return new JsonObject { ["status"] = "error", ["message"] = message };
    }

    private static FlowNodeResult Stop(FlowRun run, string message) => FlowNodeResult.Fail(Failure(run, message)["message"]!.GetValue<string>());

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        string underlying, expiryType, expiryInput, strategyType, action, product, width, priceType;
        long lots;
        double price, trigger;
        try
        {
            var v = new RuntimeOrderResolver(ctx, node.Data);
            underlying = v.Text("underlying", "NIFTY").ToUpperInvariant();
            expiryType = v.Text("expiryType", "current_week").ToLowerInvariant();
            expiryInput = v.OptionalText("expiryDate").ToUpperInvariant();
            if (expiryInput.Length == 0 && System.Text.RegularExpressions.Regex.IsMatch(expiryType.ToUpperInvariant(), Contracts.V1.ApiPatterns.ExpiryDdMmmYy)) expiryInput = expiryType.ToUpperInvariant();
            if (expiryInput.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(expiryInput, Contracts.V1.ApiPatterns.ExpiryDdMmmYy))
                throw new ArgumentException($"expiryDate must be in DDMMMYY format such as 28OCT25, got '{expiryInput}'");
            if (expiryInput.Length == 0 && !OptionVocabulary.ExpiryTypes.Contains(expiryType))
                throw new ArgumentException($"expiryType must be one of {string.Join(", ", OptionVocabulary.ExpiryTypes)}, got '{expiryType}'");
            strategyType = v.Text("strategy", "custom", "strategyType").ToLowerInvariant();
            action = v.Enum("action", OrderVocabulary.Actions, "SELL");
            lots = v.Integer("quantity", 1, 1);
            product = v.Enum("product", OrderVocabulary.Products, "NRML");
            width = v.Text("strangleWidth", "OTM2").ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(width, Contracts.V1.ApiPatterns.OptionOffset)) throw new ArgumentException($"strangleWidth must be ATM, ITM1-ITM50, or OTM1-OTM50, got '{width}'");
            priceType = v.Enum("priceType", OrderVocabulary.PriceTypes, "MARKET");
            price = v.Number("price", 0.0);
            trigger = v.Number("triggerPrice", 0.0);
            if (node.Data.ContainsKey("exchange")) v.Enum("exchange", OrderVocabulary.Exchanges);
            if (strategyType != "custom")
            {
                if (priceType is not ("MARKET" or "LIMIT")) throw new ArgumentException("generated option strategies support only MARKET and LIMIT price types");
                if (OrderVocabulary.InvalidPriceReason(priceType, price, trigger) is { } invalid) throw new ArgumentException(invalid);
            }
        }
        catch (ArgumentException ex)
        {
            return ApiNodeSupport.RuntimeOrderError(run, "Options multi-order", ex);
        }

        List<Leg>? custom = null;
        if (strategyType == "custom")
        {
            try
            {
                custom = ParseLegs(ctx, node.Data, product, priceType, price, trigger);
            }
            catch (ArgumentException ex)
            {
                return ApiNodeSupport.RuntimeOrderError(run, "Options multi-order", ex);
            }
        }

        run.Log($"Strategy: {strategyType}, Action: {action}, Quantity: {lots} lots, Product: {product}");
        var (underlyingExchange, foExchange) = OptionVocabulary.Exchanges(underlying, node.Data.Str(ctx, "exchange", ""));
        var (lot, lotError) = await OptionVocabulary.LotSizeAsync(host, run, underlying, foExchange, ct);
        if (lot is null) return Stop(run, lotError!);
        var total = lots * lot.Value;
        run.Log($"Lot size for {underlying}: {lot} -> {lots} lot(s) = {total}");

        var expiry = expiryInput;
        if (expiry.Length == 0)
        {
            var (date, _) = await OptionsOrderNode.ResolveExpiryAsync(host, run, underlying, foExchange, expiryType, ct);
            if (date is null) return Stop(run, $"Could not resolve expiry for {expiryType}");
            expiry = date;
        }

        run.Log($"Resolved expiry: {(expiryInput.Length > 0 ? expiryInput : expiryType)} -> {expiry}");
        var legs = new JsonArray();
        if (custom is not null)
        {
            var cache = new Dictionary<string, string>();
            for (var i = 0; i < custom.Count; i++)
            {
                var leg = custom[i];
                var legExpiry = leg.ExpiryDate;
                if (legExpiry.Length == 0 && leg.ExpiryType.Length > 0)
                {
                    if (!cache.TryGetValue(leg.ExpiryType, out legExpiry!))
                    {
                        var (resolved, _) = await OptionsOrderNode.ResolveExpiryAsync(host, run, underlying, foExchange, leg.ExpiryType, ct);
                        if (resolved is null) return Stop(run, $"leg {i + 1}: could not resolve expiry for {leg.ExpiryType}");
                        cache[leg.ExpiryType] = legExpiry = resolved;
                    }
                }

                var body = new JsonObject();
                if (leg.Strike is { } strike) body["strike"] = strike; else body["offset"] = leg.Offset;
                body["option_type"] = leg.OptionType;
                body["action"] = leg.Action;
                body["pricetype"] = leg.PriceType;
                body["product"] = leg.Product;
                body["price"] = leg.Price;
                body["trigger_price"] = leg.Trigger;
                body["splitsize"] = leg.SplitSize;
                body["quantity"] = leg.Lots * lot.Value;
                if (!string.IsNullOrEmpty(legExpiry)) body["expiry_date"] = legExpiry;
                legs.Add(body);
            }
        }
        else
        {
            if (priceType is "SL" or "SL-M")
                return Stop(run, $"{priceType} is not available for a generated {strategyType} strategy: its legs carry no trigger price. Use MARKET or LIMIT, or build the legs individually as custom legs where each can set its own trigger price.");
            if (OrderVocabulary.InvalidPriceReason(priceType, price, 0.0) is { } bad) return Stop(run, bad);
            JsonObject Make(string offset, string type, string side) => new()
            {
                ["offset"] = offset, ["option_type"] = type, ["action"] = side, ["quantity"] = total, ["pricetype"] = priceType, ["product"] = product, ["price"] = price, ["splitsize"] = 0,
            };
            switch (strategyType)
            {
                case "straddle": legs.Add(Make("ATM", "CE", action)); legs.Add(Make("ATM", "PE", action)); break;
                case "strangle": legs.Add(Make(width, "CE", action)); legs.Add(Make(width, "PE", action)); break;
                case "iron_condor": legs.Add(Make("OTM2", "CE", "SELL")); legs.Add(Make("OTM4", "CE", "BUY")); legs.Add(Make("OTM2", "PE", "SELL")); legs.Add(Make("OTM4", "PE", "BUY")); break;
                case "bull_call_spread": legs.Add(Make("ATM", "CE", "BUY")); legs.Add(Make("OTM2", "CE", "SELL")); break;
                case "bear_put_spread": legs.Add(Make("ATM", "PE", "BUY")); legs.Add(Make("OTM2", "PE", "SELL")); break;
                default: run.Log(strategyType == "custom" ? "Custom strategy selected but no legs provided" : $"Unknown strategy type: {strategyType}", "warning"); break;
            }
        }

        if (legs.Count == 0) return Stop(run, $"No legs generated for strategy: {strategyType}");
        run.Log($"Placing options multi-order: {underlying} {strategyType} with {legs.Count} legs");
        var result = await host.Api.CallAsync("optionsmultiorder", new JsonObject
        {
            ["underlying"] = underlying, ["exchange"] = underlyingExchange, ["expiry_date"] = expiry, ["legs"] = legs, ["strategy"] = ApiNodeSupport.Tag(node, run, host),
        }, ct);
        run.Log($"Options multi-order result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }

    private static List<Leg> ParseLegs(FlowContext ctx, JsonObject data, string product, string nodePriceType, double nodePrice, double nodeTrigger)
    {
        var raw = new RuntimeOrderResolver(ctx, data).Raw("legs", "orderLegs");
        if (raw is not JsonArray { Count: > 0 } array) throw new ArgumentException("legs must resolve to a non-empty list of objects");
        if (array.Any(l => l is not JsonObject)) throw new ArgumentException("every resolved leg must be an object");
        var result = new List<Leg>();
        for (var index = 1; index <= array.Count; index++)
        {
            try
            {
                var v = new RuntimeOrderResolver(ctx, (JsonObject)array[index - 1]!);
                var mode = v.Enum("strikeMode", ["OFFSET", "STRIKE"], "OFFSET");
                string? offset = null;
                double? strike = null;
                if (mode == "STRIKE")
                {
                    strike = v.Number("strike");
                    if (strike <= 0) throw new ArgumentException($"strike must be a positive number, got {FlowContext.PyFloat(strike.Value)}");
                }
                else
                {
                    offset = v.Text("offset").ToUpperInvariant();
                    if (!System.Text.RegularExpressions.Regex.IsMatch(offset, Contracts.V1.ApiPatterns.OptionOffset)) throw new ArgumentException($"offset must be ATM, ITM1-ITM50, or OTM1-OTM50, got '{offset}'");
                }

                var expiry = v.OptionalText("expiry").ToUpperInvariant();
                if (expiry.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(expiry, Contracts.V1.ApiPatterns.ExpiryDdMmmYy))
                    throw new ArgumentException($"expiry must be in DDMMMYY format such as 28OCT25, got '{expiry}'");
                var type = v.OptionalText("expiryType").ToLowerInvariant();
                if (type.Length > 0 && !OptionVocabulary.ExpiryTypes.Contains(type))
                    throw new ArgumentException($"expiryType must be one of {string.Join(", ", OptionVocabulary.ExpiryTypes)}, got '{type}'");
                var optionType = v.Enum("optionType", ["CE", "PE"]);
                var action = v.Enum("action", OrderVocabulary.Actions);
                var quantity = v.Integer("quantity", null, 1);
                var legProduct = v.Enum("product", OrderVocabulary.Products, product);
                var priceType = v.Enum("priceType", OrderVocabulary.PriceTypes, nodePriceType, "pricetype");
                var price = v.Number("price", nodePrice);
                var trigger = v.Number("triggerPrice", nodeTrigger, "trigger_price");
                var split = v.Integer("splitSize", 0, 0, "splitsize");
                if (OrderVocabulary.InvalidPriceReason(priceType, price, trigger) is { } invalid) throw new ArgumentException(invalid);
                result.Add(new Leg(offset, strike, expiry, type, optionType, action, quantity, priceType, legProduct, price, trigger, split));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"leg {index}: {ex.Message}");
            }
        }

        return result;
    }
}

/// <summary>Basket order: several orders sent together, from lines of SYMBOL,EXCHANGE,ACTION,QTY or a list of order objects, each checked before any is sent.</summary>
public sealed class BasketOrderNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["basketOrder"];

    private sealed class RowException(string message) : Exception(message);

    private static string Repr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? $"'{s}'" : n is null ? "None" : FlowContext.PyStr(n);

    private static JsonNode? Resolve(FlowContext ctx, JsonNode? value, string field)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var text)) value = ctx.ResolveRaw(text);
        if (value is JsonValue r && r.TryGetValue<string>(out var s) && FlowContext.HasUnresolved(s)) throw new RowException($"{field} has unresolved value {Repr(value)}");
        return value;
    }

    private static double Number(FlowContext ctx, JsonNode? value, string field)
    {
        value = Resolve(ctx, value, field);
        if (value is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) throw new RowException($"{field} must be numeric, got boolean {(b ? "True" : "False")}");
            if (v.TryGetValue<string>(out var s))
            {
                if (s.Trim().Length == 0) throw new RowException($"{field} must not be blank");
                if (!FlowData.TryParseFloat(s, out var parsed)) throw new RowException($"{field} must be numeric, got {Repr(value)}");
                return Finite(parsed, field, value);
            }

            if (FlowData.TryNumber(v, out var n)) return Finite(n, field, value);
        }

        throw new RowException($"{field} must be numeric, got {Repr(value)}");
    }

    private static double Finite(double n, string field, JsonNode? value) => double.IsFinite(n) ? n : throw new RowException($"{field} must be finite, got {Repr(value)}");

    private static string RequiredText(FlowContext ctx, JsonNode? value, string field)
    {
        value = Resolve(ctx, value, field);
        if (value is null) throw new RowException($"{field} is required");
        var text = FlowContext.PyStr(value).Trim();
        if (text.Length == 0) throw new RowException($"{field} is required");
        return text;
    }

    private static FlowNodeResult Error(FlowRun run, string message)
    {
        run.Log($"Basket order failed: {message}", "error");
        return FlowApiResults.ToResult(new JsonObject { ["status"] = "error", ["message"] = message });
    }

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        var raw = node.Data["orders"];
        if (raw is JsonValue rv && rv.TryGetValue<string>(out var rawText)) raw = ctx.ResolveRaw(rawText);
        var basketName = node.Data.Str(ctx, "basketName", "flow_basket");

        string commonProduct, commonPriceType;
        double commonPrice, commonTrigger;
        try
        {
            JsonNode? Common(string key, JsonNode? fallback) => node.Data.ContainsKey(key) ? Resolve(ctx, node.Data[key], key) : fallback;
            commonProduct = node.Data.ContainsKey("product") ? RequiredText(ctx, Common("product", null), "product").ToUpperInvariant() : "";
            commonPriceType = RequiredText(ctx, Common("priceType", JsonValue.Create("MARKET")), "priceType").ToUpperInvariant();
            if (commonProduct.Length > 0 && !OrderVocabulary.Products.Contains(commonProduct)) throw new RowException($"invalid product '{commonProduct}'");
            if (!OrderVocabulary.PriceTypes.Contains(commonPriceType)) throw new RowException($"invalid pricetype '{commonPriceType}'");
            commonPrice = Number(ctx, Common("price", JsonValue.Create(0.0)), "price");
            commonTrigger = Number(ctx, Common("triggerPrice", JsonValue.Create(0.0)), "triggerPrice");
        }
        catch (RowException e)
        {
            return Error(run, $"Basket order row 1: {e.Message}");
        }

        var rows = new List<(int Index, JsonObject Order, bool Csv)>();
        if (raw is JsonValue textValue && textValue.TryGetValue<string>(out var text))
        {
            var lines = text.Trim().Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length < 4) return Error(run, $"Basket order row {i + 1}: expected SYMBOL,EXCHANGE,ACTION,QTY");
                rows.Add((i + 1, new JsonObject { ["symbol"] = parts[0], ["exchange"] = parts[1], ["action"] = parts[2], ["quantity"] = parts[3] }, true));
            }
        }
        else if (raw is JsonArray list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is not JsonObject order) return Error(run, $"Basket order row {i + 1}: must be an order object");
                rows.Add((i + 1, order, false));
            }
        }

        if (rows.Count == 0)
        {
            run.Log("Basket order failed: No valid orders", "error");
            return FlowApiResults.ToResult(new JsonObject { ["status"] = "error", ["message"] = "No valid orders to place" });
        }

        var orders = new JsonArray();
        foreach (var (index, order, csv) in rows)
        {
            JsonNode? RowValue(string key, JsonNode? fallback, params string[] aliases)
            {
                if (csv) return fallback;
                foreach (var candidate in new[] { key }.Concat(aliases))
                    if (order.ContainsKey(candidate)) return Resolve(ctx, order[candidate], candidate);
                return fallback;
            }

            try
            {
                var symbol = RequiredText(ctx, order["symbol"], "symbol");
                var exchange = RequiredText(ctx, order["exchange"], "exchange").ToUpperInvariant();
                var action = RequiredText(ctx, order["action"], "action").ToUpperInvariant();
                var quantity = (long)Number(ctx, Resolve(ctx, order["quantity"], "quantity"), "quantity");
                if (quantity <= 0) throw new RowException($"quantity must be positive, got {quantity}");
                var product = RequiredText(ctx, RowValue("product", JsonValue.Create(commonProduct.Length > 0 ? commonProduct : OrderVocabulary.DefaultProduct(exchange))), "product").ToUpperInvariant();
                var priceType = RequiredText(ctx, RowValue("pricetype", JsonValue.Create(commonPriceType), "priceType"), "pricetype").ToUpperInvariant();
                if (!OrderVocabulary.Exchanges.Contains(exchange)) throw new RowException($"invalid exchange '{exchange}'");
                if (!OrderVocabulary.Actions.Contains(action)) throw new RowException($"invalid action '{action}'");
                if (!OrderVocabulary.Products.Contains(product)) throw new RowException($"invalid product '{product}'");
                if (!OrderVocabulary.PriceTypes.Contains(priceType)) throw new RowException($"invalid pricetype '{priceType}'");
                var price = Number(ctx, RowValue("price", JsonValue.Create(commonPrice)), "price");
                var trigger = Number(ctx, RowValue("triggerprice", JsonValue.Create(commonTrigger), "triggerPrice"), "triggerprice");
                if (OrderVocabulary.InvalidPriceReason(priceType, price, trigger) is { } invalid) throw new RowException(invalid);
                orders.Add(new JsonObject
                {
                    ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = action, ["quantity"] = quantity, ["product"] = product, ["pricetype"] = priceType, ["price"] = price, ["trigger_price"] = trigger,
                });
            }
            catch (RowException e)
            {
                return Error(run, $"Basket order row {index}: {e.Message}");
            }
        }

        run.Log($"Placing basket order '{basketName}' with {orders.Count} orders");
        for (var i = 0; i < orders.Count; i++) run.Log($"  Order {i + 1}: {orders[i]!["symbol"]} {orders[i]!["exchange"]} {orders[i]!["action"]} qty={orders[i]!["quantity"]}");
        var result = await host.Api.CallAsync("basketorder", new JsonObject { ["strategy"] = basketName, ["orders"] = orders }, ct);
        run.Log($"Basket order result: {result.ToJsonString()}", ApiNodeSupport.Level(result));
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }
}

/// <summary>Margin: what a position, or a whole basket of positions, would need.</summary>
public sealed class MarginNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["margin"];

    private static string NumericText(double n) => n == Math.Floor(n) && Math.Abs(n) < 1e15 ? ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture) : FlowContext.PyFloat(n);

    private static JsonArray? ParsePositions(FlowNode node, FlowContext ctx)
    {
        var key = new[] { "positionsJson", "positions" }.FirstOrDefault(k => node.Data[k] is not null);
        if (key is null) return null;
        var raw = node.Data[key];
        if (raw is JsonValue rv && rv.TryGetValue<string>(out var t0)) raw = ctx.ResolveRaw(t0);
        if (raw is JsonValue sv && sv.TryGetValue<string>(out var text))
        {
            if (FlowContext.HasUnresolved(text)) throw new ArgumentException($"Margin positions has unresolved value '{text}'.");
            text = text.Trim();
            if (text.Length == 0) throw new ArgumentException("Margin positions is empty after variable interpolation; check the {{variable}} references in the basket.");
            try
            {
                raw = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new ArgumentException($"Margin positions is not valid JSON after variable interpolation: {e.Message}");
            }
        }

        if (raw is JsonObject single) raw = new JsonArray(single.DeepClone());
        if (raw is not JsonArray list) throw new ArgumentException("Margin positions must be a JSON array of position objects.");
        if (list.Count == 0) throw new ArgumentException("Margin positions is an empty basket; add at least one position.");
        if (list.Any(e => e is not JsonObject)) throw new ArgumentException("Margin positions must all be objects; the basket contains a non-object entry.");

        var result = new JsonArray();
        for (var index = 1; index <= list.Count; index++)
        {
            var position = (JsonObject)list[index - 1]!;
            try
            {
                var v = new RuntimeOrderResolver(ctx, position);
                var symbol = v.Text("symbol").ToUpperInvariant();
                var exchange = v.Enum("exchange", OrderVocabulary.Exchanges);
                var action = v.Enum("action", OrderVocabulary.Actions);
                var quantity = v.Integer("quantity", null, 1);
                var product = v.Enum("product", OrderVocabulary.Products);
                var priceType = v.Enum("pricetype", OrderVocabulary.PriceTypes);
                var price = v.Number("price", 0.0);
                var trigger = v.Number("trigger_price", 0.0, "triggerPrice");
                if (OrderVocabulary.InvalidPriceReason(priceType, price, trigger) is { } invalid) throw new ArgumentException(invalid);
                var resolved = (JsonObject)position.DeepClone();
                resolved["symbol"] = symbol;
                resolved["exchange"] = exchange;
                resolved["action"] = action;
                resolved["quantity"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                resolved["product"] = product;
                resolved["pricetype"] = priceType;
                if (position.ContainsKey("price")) resolved["price"] = NumericText(price);
                if (position.ContainsKey("trigger_price") || position.ContainsKey("triggerPrice"))
                {
                    resolved.Remove("triggerPrice");
                    resolved["trigger_price"] = NumericText(trigger);
                }

                result.Add(resolved);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Margin position {index}: {ex.Message}");
            }
        }

        return result;
    }

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        JsonArray? positions;
        try
        {
            positions = ParsePositions(node, ctx);
        }
        catch (ArgumentException ex)
        {
            run.Log($"Margin failed: {ex.Message}", "error");
            return FlowApiResults.ToResult(new JsonObject { ["status"] = "error", ["message"] = ex.Message });
        }

        var symbol = node.Data.Str(ctx, "symbol", "");
        var exchange = node.Data.Str(ctx, "exchange", "NSE");
        var quantity = MarketInfoNodes.Int(node, ctx, "quantity", 1);
        var priceRaw = node.Data["price"];
        string priceText;
        if (priceRaw is null || priceRaw is JsonValue e && e.TryGetValue<string>(out var es) && es.Length == 0) priceText = "0";
        else if (priceRaw is JsonValue ps && ps.TryGetValue<string>(out var pt)) priceText = FlowData.TryParseFloat(ctx.Interpolate(pt), out var parsed) ? FlowContext.PyFloat(parsed) : "0";
        else priceText = FlowContext.PyFloat(FlowData.ToDouble(priceRaw));
        var product = node.Data.Str(ctx, "product", OrderVocabulary.DefaultProduct(exchange));
        var action = node.Data.Str(ctx, "action", "BUY");
        var priceType = node.Data.Str(ctx, "priceType", "MARKET");

        if (positions is { Count: > 0 }) run.Log($"Calculating margin for a basket of {positions.Count} position(s)");
        else if (symbol.Length == 0)
        {
            const string message = "Margin node has no positions and no symbol to price.";
            run.Log($"Margin failed: {message}", "error");
            return FlowApiResults.ToResult(new JsonObject { ["status"] = "error", ["message"] = message });
        }
        else
        {
            run.Log($"Calculating margin for: {symbol} ({exchange})");
            positions = new JsonArray(new JsonObject
            {
                ["exchange"] = exchange, ["symbol"] = symbol, ["action"] = action.ToUpperInvariant(), ["quantity"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), ["product"] = product,
                ["pricetype"] = priceType, ["price"] = priceText,
            });
        }

        var result = await host.Api.CallAsync("margin", new JsonObject { ["positions"] = positions }, ct);
        run.Log($"Margin result: {result.ToJsonString()}");
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }
}

/// <summary>HTTP request: calls an outside address and keeps the answer. The address is checked first so a workflow (or whoever can fire its webhook) cannot reach this machine, the local network or cloud metadata.</summary>
public sealed class HttpRequestNode : IFlowNode
{
    public const int DefaultTimeoutMs = 30_000;
    public const int MaxTimeoutMs = 60_000;

    public IReadOnlyList<string> Types => ["httpRequest"];

    /// <summary>The address without credentials or query string, for logs that are stored and shown.</summary>
    public static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "<unparseable url>";
        var host = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var redacted = $"{uri.Scheme}://{host}{uri.AbsolutePath}";
        return uri.Query.Length > 1 ? redacted + "?<redacted>" : redacted;
    }

    private static string KindOf(JsonNode? n) => n switch { null => "NoneType", JsonArray => "list", JsonObject => "dict", JsonValue v when v.TryGetValue<bool>(out _) => "bool", JsonValue v when v.TryGetValue<string>(out _) => "str", JsonValue v when v.TryGetValue<long>(out _) => "int", _ => "float" };

    private static FlowNodeResult Error(string message) => FlowApiResults.ToResult(new JsonObject { ["status"] = "error", ["message"] = message });

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var host = ApiNodeSupport.HostOf(run);
        var ctx = run.Context;
        var method = node.Data.Str(ctx, "method", "GET").ToUpperInvariant();
        var url = node.Data["url"] is JsonValue uv ? FlowContext.PyStr(uv) : "";
        var headersRaw = node.Data["headers"];
        var body = node.Data["body"];
        var timeoutMs = MarketInfoNodes.Int(node, ctx, "timeout", DefaultTimeoutMs);
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;
        var timeout = TimeSpan.FromMilliseconds(Math.Min(timeoutMs, MaxTimeoutMs));
        if (url.Length == 0) return Error("No URL specified");

        // The headers are parsed before their values are interpolated, never after: a substituted value carrying a quote must not become structure.
        if (headersRaw is JsonValue hv && hv.TryGetValue<string>(out var headerText))
        {
            headerText = headerText.Trim();
            if (headerText.Length == 0) headersRaw = new JsonObject();
            else
            {
                try
                {
                    headersRaw = JsonNode.Parse(headerText);
                }
                catch (System.Text.Json.JsonException e)
                {
                    var message = $"headers is not valid JSON: {e.Message}";
                    run.Log($"HTTP request aborted: {message}", "error");
                    return Error(message);
                }
            }
        }

        var headers = new Dictionary<string, string>();
        if (headersRaw is null) headersRaw = new JsonObject();
        if (headersRaw is JsonObject map)
            foreach (var (key, value) in map) headers[key] = ctx.Interpolate(FlowContext.PyStr(value));
        else
        {
            var message = $"headers must be a JSON object, got {KindOf(headersRaw)}";
            run.Log($"HTTP request aborted: {message}", "error");
            return Error(message);
        }

        // The address is interpolated exactly once, here: a second pass would expand a reference a caller smuggled into a value.
        url = ctx.Interpolate(url);
        string? text = null;
        JsonNode? json = null;
        if (body is JsonValue bv && bv.TryGetValue<string>(out var bodyText)) text = bodyText.Length > 0 ? ctx.Interpolate(bodyText) : "";
        else if (body is not null) json = body;

        if (host.Http is null) return Error("This platform cannot make outbound requests from a workflow.");
        if (await host.Http.CheckDestinationAsync(url, ct) is { } rejection)
        {
            var message = $"refusing to send this request: {rejection}";
            run.Log($"HTTP request aborted: {message}", "error");
            return Error(message);
        }

        run.Log($"HTTP {method} {Redact(url)}");
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) return Error($"Unsupported method: {method}");
        JsonNode? payload = null;
        string? raw = null;
        if (method is "POST" or "PUT" or "PATCH")
        {
            if (json is not null) payload = json;
            else if (string.IsNullOrEmpty(text)) payload = new JsonObject();
            else
            {
                try { payload = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException) { raw = text; }
            }
        }

        FlowHttpResponse response;
        try
        {
            response = await host.Http.SendAsync(method, url, headers, payload, raw, timeout, ct);
        }
        catch (FlowHttpException e)
        {
            return Error(e.Message);
        }

        JsonNode? data;
        try { data = JsonNode.Parse(response.Text); }
        catch (System.Text.Json.JsonException) { data = JsonValue.Create(response.Text); }
        var result = new JsonObject { ["status"] = response.IsSuccess ? "success" : "error", ["statusCode"] = response.Status, ["data"] = data };
        ApiNodeSupport.Store(node, run, result);
        return FlowApiResults.ToResult(result);
    }
}
