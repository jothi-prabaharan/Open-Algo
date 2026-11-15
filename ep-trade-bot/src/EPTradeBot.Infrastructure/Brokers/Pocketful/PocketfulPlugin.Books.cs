using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Pocketful;

/// <summary>
/// Pocketful's books and funds (broker/pocketful api/order_api.py, api/funds.py, mapping/order_data.py). The order book is two reads (completed, pending) joined; the trade book, positions and holdings are one each. A row's symbol is the master contract's name for the
/// broker's trading symbol, which is nothing when the master does not have it (the order book and trade book then carry no symbol, the position book and holdings keep the broker's). Values a row carries are passed on as they came.
/// </summary>
public sealed partial class PocketfulPlugin
{
    private static JsonNode? Raw(JsonObject row, string key, JsonNode? fallback) => row.TryGetPropertyValue(key, out var v) ? v?.DeepClone() : fallback;

    /// <summary>Python's <c>row.get(key, default)</c> read as text, for a key that holds text.</summary>
    private static string TextOf(JsonObject row, string key) => row.TryGetPropertyValue(key, out var v) ? Str(v) ?? "" : "";

    private async Task<JsonNode?> OaSymbolAsync(string? brokerSymbol, string? exchange, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(brokerSymbol) || string.IsNullOrEmpty(exchange) ? null : (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol;

    /// <summary>One read that must come back as a JSON object. Anything else is an error that says what to do.</summary>
    private async Task<JsonObject> ReadObjectAsync(BrokerSession session, string path, IEnumerable<KeyValuePair<string, string>> query, string what, CancellationToken cancellationToken)
    {
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, path, query, null, cancellationToken);
        if (status != 200) throw new BrokerException($"{Display} did not give the {what} ({Reason(body) ?? "no reason was given"}). Try again in a moment.", status >= 400 ? status : 502);
        return body as JsonObject ?? throw new BrokerException($"{Display} sent the {what} in a form that could not be read. Try again in a moment.", 502);
    }

    // ---- order book ------------------------------------------------------------------------------------------

    private static List<JsonObject> OrdersOf(JsonNode? body) =>
        body is JsonObject o && o["data"] is JsonObject data && data["orders"] is JsonArray orders ? orders.OfType<JsonObject>().ToList() : [];

    private async Task<List<JsonObject>> ReadOrdersAsync(BrokerSession session, string clientId, string type, CancellationToken cancellationToken)
    {
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/api/v1/orders", [new("client_id", clientId), new("type", type)], null, cancellationToken);
        if (status >= 400 || body is not JsonObject answer || Str(answer["status"]) == "error")
            throw new BrokerException($"{Display} did not give the {type} orders ({Reason(body) ?? "no reason was given"}). Try again in a moment.", status >= 400 ? status : 502);
        return OrdersOf(answer);
    }

    private static string OrderStatus(string status, string mode)
    {
        if (status.Contains("CANCEL_CONFIRMED", StringComparison.Ordinal)) return "cancelled";
        if (status.Contains("COMPLETE", StringComparison.Ordinal)) return "complete";
        if (status.Contains("REJECTED", StringComparison.Ordinal)) return "rejected";
        if (status.Contains("TRIGGER PENDING", StringComparison.Ordinal)) return "trigger pending";
        if (status.Contains("OPEN", StringComparison.Ordinal) || status.Contains("PENDING", StringComparison.Ordinal) || status.Contains("AMO_SUBMIT", StringComparison.Ordinal) || status.Contains("MODIFY", StringComparison.Ordinal) || mode == "NEW") return "open";
        return status.Contains("CANCEL", StringComparison.Ordinal) ? "cancelled" : "unknown";
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var clientId = await ClientIdAsync(session, cancellationToken);
        // Both reads are made, as OpenAlgo makes them; a book with one half missing is not shown as the whole book.
        BrokerException? failed = null;
        var orders = new List<JsonObject>();
        foreach (var type in new[] { "completed", "pending" })
        {
            try
            {
                orders.AddRange(await ReadOrdersAsync(session, clientId, type, cancellationToken));
            }
            catch (BrokerException ex)
            {
                failed ??= ex;
            }
        }

        if (failed is not null) throw failed;

        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0, cancelled = 0;
        var rows = new JsonArray();
        foreach (var order in orders)
        {
            // The symbol OpenAlgo names the order's instrument by.
            if (order.ContainsKey("exchange"))
            {
                var exchange = TextOf(order, "exchange");
                var key = order.ContainsKey("trading_symbol") ? "trading_symbol" : order.ContainsKey("tradingsymbol") ? "tradingsymbol" : null;
                if (key is not null && Truthy(order[key]))
                {
                    var oa = await OaSymbolAsync(Str(order[key]), exchange, cancellationToken);
                    order["tradingsymbol"] = oa?.DeepClone();
                    if (order.ContainsKey("trading_symbol")) order["trading_symbol"] = oa?.DeepClone();
                }
            }

            var side = order.ContainsKey("transaction_type") ? Str(order["transaction_type"]) : order.ContainsKey("order_side") ? Str(order["order_side"]) : null;
            if (!string.IsNullOrEmpty(side))
            {
                if (side.Equals("BUY", StringComparison.OrdinalIgnoreCase)) buy++;
                else if (side.Equals("SELL", StringComparison.OrdinalIgnoreCase)) sell++;
            }

            var statusKey = order.ContainsKey("status") ? "status" : order.ContainsKey("order_status") ? "order_status" : null;
            var counted = statusKey is null ? "" : (Truthy(order[statusKey]) ? (Str(order[statusKey]) ?? "").ToUpperInvariant() : "");
            var mode = TextOf(order, "mode").ToUpperInvariant();
            var done = false;
            if (counted.Length > 0)
            {
                if (counted.Contains("CANCEL_CONFIRMED", StringComparison.Ordinal)) { cancelled++; done = true; }
                else if (counted.Contains("COMPLETE", StringComparison.Ordinal)) { completed++; done = true; }
                else if (counted.Contains("REJECTED", StringComparison.Ordinal)) { rejected++; done = true; }
            }

            if (!done && (counted.Contains("OPEN", StringComparison.Ordinal) || counted.Contains("NEW", StringComparison.Ordinal) || counted.Contains("PENDING", StringComparison.Ordinal) || counted.Contains("SUBMIT", StringComparison.Ordinal) || counted.Contains("MODIFY", StringComparison.Ordinal) || mode == "NEW"))
                open++;

            var status = TextOf(order, "order_status").ToUpperInvariant();
            var action = order.TryGetPropertyValue("order_side", out var sideNode) ? Str(sideNode) ?? "" : TextOf(order, "transaction_type");
            action = action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "BUY" : action.Equals("SELL", StringComparison.OrdinalIgnoreCase) ? "SELL" : action;
            var symbol = order.ContainsKey("trading_symbol") ? Raw(order, "trading_symbol", "") : Raw(order, "tradingsymbol", "");
            var average = Truthy(order["average_price"]) && order.ContainsKey("average_price") ? Raw(order, "average_price", 0.0) : Raw(order, "average_trade_price", 0.0);
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Raw(order, "exchange", ""), ["action"] = action, ["quantity"] = Raw(order, "quantity", 0), ["price"] = Raw(order, "price", 0.0), ["trigger_price"] = Raw(order, "trigger_price", 0.0),
                ["pricetype"] = Raw(order, "order_type", ""), ["product"] = Raw(order, "product", ""), ["orderid"] = order.ContainsKey("oms_order_id") ? Raw(order, "oms_order_id", "") : Raw(order, "order_id", ""),
                ["order_status"] = OrderStatus(status, mode), ["timestamp"] = order.ContainsKey("order_entry_time") ? Raw(order, "order_entry_time", "") : Raw(order, "order_timestamp", ""), ["filled_quantity"] = Raw(order, "filled_quantity", 0),
                ["pending_quantity"] = Raw(order, "remaining_quantity", 0), ["average_price"] = average,
            });
        }

        return new BrokerBook(rows, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected, ["total_cancelled_orders"] = cancelled });
    }

    // ---- trade book ------------------------------------------------------------------------------------------

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var clientId = await ClientIdAsync(session, cancellationToken);
        var answer = await ReadObjectAsync(session, "/api/v1/trades", [new("client_id", clientId)], "trade book", cancellationToken);
        var trades = answer["data"] is JsonObject data && data["trades"] is JsonArray list ? list.OfType<JsonObject>().ToList() : [];
        var rows = new JsonArray();
        foreach (var trade in trades)
        {
            var exchange = TextOf(trade, "exchange");
            var symbol = TextOf(trade, "trading_symbol");
            JsonNode? name = trade.ContainsKey("tradingsymbol") ? Raw(trade, "tradingsymbol", "") : "";
            if (symbol.Length > 0)
            {
                name = symbol;
                if (exchange.Length > 0) name = await OaSymbolAsync(symbol, exchange, cancellationToken);
            }

            JsonNode? Pick(string main, string alternative) => Truthy(trade[main]) ? Raw(trade, main, null) : trade.ContainsKey(alternative) ? Raw(trade, alternative, null) : Raw(trade, main, null);
            var side = Truthy(trade["transaction_type"]) ? Str(trade["transaction_type"]) ?? "" : trade.ContainsKey("order_side") ? Str(trade["order_side"]) ?? "" : TextOf(trade, "transaction_type");
            var quantity = (long)Math.Truncate(Float(Pick("fill_quantity", "trade_quantity"), 0));
            var price = Float(Pick("avg_price", "trade_price"), 0);
            rows.Add(new JsonObject
            {
                ["symbol"] = name?.DeepClone(), ["exchange"] = Raw(trade, "exchange", ""), ["product"] = Raw(trade, "product", ""), ["action"] = side.ToUpperInvariant(), ["quantity"] = quantity, ["average_price"] = price,
                ["trade_id"] = Pick("trade_id", "trade_number") ?? "", ["orderid"] = Pick("order_id", "oms_order_id") ?? "", ["timestamp"] = Pick("fill_timestamp", "trade_time") ?? "", ["trade_value"] = quantity > 0 && price > 0 ? quantity * price : 0.0,
            });
        }

        return rows;
    }

    // ---- positions -------------------------------------------------------------------------------------------

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var clientId = await ClientIdAsync(session, cancellationToken);
        var answer = await ReadObjectAsync(session, "/api/v1/positions", [new("client_id", clientId), new("type", "live")], "position book", cancellationToken);
        if (Str(answer["status"]) != "success") throw new BrokerException($"{Display} did not give the position book ({Reason(answer) ?? "no reason was given"}). Try again in a moment.", 502);
        var positions = answer["data"] switch { JsonArray list => list.OfType<JsonObject>().ToList(), JsonObject data when data["positions"] is JsonArray nested => nested.OfType<JsonObject>().ToList(), _ => [] };
        var rows = new JsonArray();
        foreach (var position in positions)
        {
            var exchange = TextOf(position, "exchange");
            var symbol = position.ContainsKey("trading_symbol") ? TextOf(position, "trading_symbol") : TextOf(position, "tradingsymbol");
            JsonNode? name = symbol;
            if (symbol.Length > 0 && exchange.Length > 0 && await OaSymbolAsync(symbol, exchange, cancellationToken) is { } oa) name = oa;
            var average = position.ContainsKey("average_buy_price") && Float(position["average_buy_price"]) > 0 ? Float(position["average_buy_price"])
                : position.ContainsKey("average_sell_price") && Float(position["average_sell_price"]) > 0 ? Float(position["average_sell_price"])
                : Float(position["average_price"]);
            rows.Add(new JsonObject
            {
                ["symbol"] = name?.DeepClone() ?? (symbol.Length > 0 ? symbol : ""), ["exchange"] = exchange, ["product"] = Raw(position, "product", ""),
                ["quantity"] = position.ContainsKey("net_quantity") ? Raw(position, "net_quantity", null) : Raw(position, "quantity", "0"), ["average_price"] = BookFormatting.Fixed2(average),
            });
        }

        return rows;
    }

    // ---- holdings --------------------------------------------------------------------------------------------

    private static List<JsonObject>? HoldingsOf(JsonObject answer)
    {
        if (answer["data"] is JsonObject data && data["holdings"] is JsonArray nested) return nested.OfType<JsonObject>().ToList();
        if (answer["data"] is JsonArray list) return list.OfType<JsonObject>().ToList();
        if (answer["holdings"] is JsonArray top) return top.OfType<JsonObject>().ToList();
        if (answer["data"] is JsonObject any)
            foreach (var (_, value) in any)
                if (value is JsonArray first) return first.OfType<JsonObject>().ToList();
        return [];
    }

    private static double? Parsed(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var text) => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        _ => BookFormatting.Number(node),
    };

    private static double FirstNumber(JsonObject holding, params string[] fields)
    {
        foreach (var field in fields)
            if (holding.ContainsKey(field) && holding[field] is not null && Parsed(holding[field]) is { } n) return n;
        return 0;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var clientId = await ClientIdAsync(session, cancellationToken);
        var (status, body, text) = await SendAsync(session, HttpMethod.Get, "/api/v1/holdings", [new("client_id", clientId)], null, cancellationToken);
        if (status >= 400) throw new BrokerException($"{Display} did not give the holdings ({Reason(body) ?? "no reason was given"}). Try again in a moment.", status);
        if (body is null)
        {
            if (text.Contains("<!doctype html>", StringComparison.Ordinal)) throw new BrokerException($"{Display} sent a web page instead of the holdings. Sign in to {Display} again, then try again.", 502);
            return new BrokerBook([], Totals([]));
        }

        if (body is not JsonObject answer) throw new BrokerException($"{Display} sent the holdings in a form that could not be read. Try again in a moment.", 502);
        if (Str(answer["status"]) == "error") throw new BrokerException($"{Display} did not give the holdings ({Reason(answer) ?? "no reason was given"}). Try again in a moment.", 502);
        var holdings = HoldingsOf(answer) ?? [];
        var rows = new JsonArray();
        foreach (var holding in holdings)
        {
            string? name = null;
            foreach (var field in new[] { "tradingsymbol", "trading_symbol", "symbol" })
                if (holding.ContainsKey(field) && Truthy(holding[field])) { name = Str(holding[field]); break; }
            if (name is null && holding["instrument_details"] is JsonObject details)
                name = details.ContainsKey("trading_symbol") ? Str(details["trading_symbol"]) : details.ContainsKey("symbol") ? Str(details["symbol"]) : null;
            if (string.IsNullOrEmpty(name)) continue;
            var exchange = holding.ContainsKey("exchange") ? TextOf(holding, "exchange") : "NSE";
            if (exchange.Length > 0)
            {
                if (name.EndsWith("-EQ", StringComparison.Ordinal)) name = name.Replace("-EQ", "", StringComparison.Ordinal);
                if (await OaSymbolAsync(name, exchange, cancellationToken) is { } oa && Str(oa) is { Length: > 0 } mapped) name = mapped;
            }

            long quantity = 0;
            foreach (var field in new[] { "quantity", "free_quantity", "qty" })
                if (holding.ContainsKey(field) && holding[field] is not null && Parsed(holding[field]) is { } q) { quantity = (long)Math.Truncate(q); break; }
            var average = FirstNumber(holding, "average_price", "buy_avg", "avg_price", "buy_price");
            var last = FirstNumber(holding, "last_price", "ltp", "current_price", "market_price");
            var computed = quantity > 0 && average > 0 ? (last - average) * quantity : 0.0;
            var pnl = holding.ContainsKey("pnl") && holding["pnl"] is not null ? Parsed(holding["pnl"]) ?? computed : computed;
            var computedPercent = average > 0 ? (last - average) / average * 100 : 0.0;
            double percent;
            if (holding.ContainsKey("pnl_percent") || holding.ContainsKey("pnl_percentage"))
            {
                var source = Truthy(holding["pnl_percent"]) ? holding["pnl_percent"] : holding.ContainsKey("pnl_percentage") ? holding["pnl_percentage"] : JsonValue.Create(0.0);
                percent = Parsed(source) ?? computedPercent;
            }
            else percent = computedPercent;

            rows.Add(new JsonObject
            {
                ["symbol"] = name, ["exchange"] = exchange, ["quantity"] = quantity, ["average_price"] = BookFormatting.RoundPy(average, 2), ["last_price"] = BookFormatting.RoundPy(last, 2), ["product"] = "CNC", ["pnl"] = BookFormatting.RoundPy(pnl, 2),
                ["pnlpercent"] = BookFormatting.RoundPy(percent, 2), ["close_price"] = Float(Truthy(holding["close_price"]) ? holding["close_price"] : holding["previous_close"]), ["isin"] = Raw(holding, "isin", ""), ["day_change"] = Float(holding["day_change"]),
                ["day_change_percentage"] = Float(holding["day_change_percentage"]),
            });
        }

        return new BrokerBook(rows, Totals(rows));
    }

    private static JsonObject Totals(JsonArray rows)
    {
        if (rows.Count == 0) return new JsonObject { ["totalholdingvalue"] = 0.0, ["totalinvvalue"] = 0.0, ["totalprofitandloss"] = 0.0, ["totalpnlpercentage"] = 0.0 };
        double value = 0, invested = 0, pnl = 0;
        foreach (var row in rows.OfType<JsonObject>())
        {
            var quantity = Float(row["quantity"]);
            value += Float(row["last_price"]) * quantity;
            invested += Float(row["average_price"]) * quantity;
            pnl += Float(row["pnl"]);
        }

        return new JsonObject { ["totalholdingvalue"] = BookFormatting.RoundPy(value, 2), ["totalinvvalue"] = BookFormatting.RoundPy(invested, 2), ["totalprofitandloss"] = BookFormatting.RoundPy(pnl, 2), ["totalpnlpercentage"] = BookFormatting.RoundPy(invested > 0 ? pnl / invested * 100 : 0.0, 2) };
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var clientId = await ClientIdAsync(session, cancellationToken);
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/api/v2/funds/view", [new("client_id", clientId), new("type", "all")], null, cancellationToken);
        if (status >= 400 || !Succeeded(body)) throw new BrokerException($"{Display} did not give the funds ({Reason(body) ?? "no reason was given"}). Try again in a moment.", status >= 400 ? status : 502);
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if ((body as JsonObject)?["data"] is JsonObject data && data["values"] is JsonArray values)
        {
            foreach (var item in values)
            {
                if (item is not JsonArray pair || pair.Count < 2 || Parsed(pair[1]) is not { } number || Str(pair[0]) is not { } key)
                    throw new BrokerException($"{Display} sent funds that could not be read. Try again in a moment.", 502);
                map[key] = number;
            }
        }

        double Of(string key) => map.GetValueOrDefault(key, 0.0);
        return new JsonObject
        {
            ["availablecash"] = BookFormatting.Fixed2(Of("Available Margin")), ["collateral"] = BookFormatting.Fixed2(Of("Total Pledge Collateral")), ["m2munrealized"] = BookFormatting.Fixed2(Of("unrealized_mtm")),
            ["m2mrealized"] = BookFormatting.Fixed2(Of("realized_mtm")), ["utiliseddebits"] = BookFormatting.Fixed2(Of("Margin Used")),
        };
    }
}
