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
    public static IEnumerable<IFlowNode> All() => [new PlaceOrderNodes(), new IndicatorNode(), new ModifyOrderNode(), new CancelOrderNodes(), new ClosePositionsNode(), new DataNodes(), new CheckNodes()];
}
