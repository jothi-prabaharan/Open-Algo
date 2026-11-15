using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Groww;

/// <summary>Groww's order book, trade book, positions, holdings and funds (broker/groww/api/order_api.py, funds.py and mapping/order_data.py).</summary>
public sealed partial class GrowwPlugin
{
    private const int OrderPage = 100;
    private const int TradePage = 50;
    private const int LtpBatch = 50;

    // ---- what a message that is not a failure looks like -------------------------------------------------------

    private static readonly string[] EmptyMarkers =
    [
        "no data", "nodata", "no_data", "no-data", "no position", "no open position", "no record", "have any position", "have any open position", "data not found", "record not found", "positions not found",
    ];

    private static readonly string[] MessageFields = ["emsg", "message", "msg", "errorMessage", "statusMessage", "errMsg", "description", "s", "error", "error.message", "error.msg"];

    /// <summary>OpenAlgo's says_no_positions: a short message in one of the fields a broker puts its message in that names an empty book.</summary>
    private static bool SaysNoPositions(JsonNode? body, IEnumerable<string>? fields = null)
    {
        if (body is not JsonObject root) return false;
        foreach (var path in fields ?? MessageFields)
        {
            JsonNode? node = root;
            foreach (var part in path.Split('.')) node = node is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next : null;
            if (node is JsonValue v && v.TryGetValue<string>(out var text) && text.Length <= 2000 && EmptyMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
        }

        return false;
    }

    /// <summary>A finite number from a Groww field, or 0 (OpenAlgo's _num).</summary>
    private static double Num(JsonNode? node)
    {
        if (!Truthy(node)) return 0;
        var value = Float(node, double.NaN);
        return double.IsFinite(value) ? value : 0;
    }

    private async Task<string?> OpenAlgoSymbolAsync(string? brokerSymbol, string exchange, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(brokerSymbol) ? null : (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol;

    // ---- reading a list in pages -------------------------------------------------------------------------------

    /// <summary>Every page of a Groww list. The items read and, when a page failed, why; a short page ends the list.</summary>
    private async Task<(List<JsonObject> Items, string? Failure)> PagedAsync(BrokerSession session, string path, string segment, string listKey, int pageSize, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        for (var page = 0; ; page++)
        {
            var query = new[] { KeyValuePair.Create("segment", segment), KeyValuePair.Create("page", page.ToString(CultureInfo.InvariantCulture)), KeyValuePair.Create("page_size", pageSize.ToString(CultureInfo.InvariantCulture)) };
            var (status, body, _) = await SendAsync(session, HttpMethod.Get, path, "non_trading", query, null, cancellationToken);
            if (status != 200 || body is not JsonObject answer || Str(answer["status"]) != "SUCCESS") return (items, ReasonOf(body, $"HTTP {status}"));
            var batch = (Payload(body)[listKey] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            items.AddRange(batch);
            if (batch.Count < pageSize) return (items, null);
        }
    }

    /// <summary>
    /// The day's orders from both segments, each with OpenAlgo's exchange and symbol. A CASH read that fails (and does not just say there are none) is the failure, with what was read; an F&amp;O read that fails is skipped, as it
    /// fails on accounts without F&amp;O.
    /// </summary>
    private async Task<(List<JsonObject> Orders, string? Failure)> ReadOrderListAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var all = new List<JsonObject>();
        foreach (var segment in new[] { "CASH", "FNO" })
        {
            var (orders, failure) = await PagedAsync(session, "/v1/order/list", segment, "order_list", OrderPage, cancellationToken);
            if (failure is not null && !SaysNoPositions(new JsonObject { ["message"] = failure }) && segment == "CASH") return ([], failure);
            all.AddRange(orders);
        }

        foreach (var order in all)
        {
            if (!order.ContainsKey("trading_symbol")) continue;
            var brokerSymbol = Str(order["trading_symbol"]) ?? "";
            var exchange = OpenAlgoExchange(Str(order["exchange"]) ?? "", Str(order["segment"]) ?? "");
            order["exchange"] = exchange;
            order["symbol"] = await OpenAlgoSymbolAsync(brokerSymbol, exchange, cancellationToken) is { Length: > 0 } oa ? oa : brokerSymbol;
        }

        return (all, null);
    }

    // ---- order book ------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Statuses = new()
    {
        ["NEW"] = "open", ["ACKED"] = "open", ["OPEN"] = "open", ["APPROVED"] = "open", ["TRIGGER_PENDING"] = "trigger pending", ["MODIFICATION_REQUESTED"] = "open", ["CANCELLATION_REQUESTED"] = "open",
        ["EXECUTED"] = "complete", ["DELIVERY_AWAITED"] = "complete", ["COMPLETED"] = "complete", ["CANCELLED"] = "cancelled", ["REJECTED"] = "rejected", ["FAILED"] = "rejected",
    };

    private static readonly Dictionary<string, string> PriceTypes = new() { ["MARKET"] = "MARKET", ["LIMIT"] = "LIMIT", ["SL"] = "SL", ["SL_M"] = "SL-M" };

    private static JsonNode? Given(JsonObject row, string key, JsonNode? fallback) => row.TryGetPropertyValue(key, out var v) ? v?.DeepClone() : fallback;

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (orders, failure) = await ReadOrderListAsync(session, cancellationToken);
        if (failure is not null) throw new BrokerException($"Could not read the {Display} order book: {failure}", 500);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var rows = new JsonArray();
        foreach (var order in orders)
        {
            var orderType = Str(order["order_type"]) ?? "";
            var raw = Str(order["order_status"]) ?? "";
            var status = Statuses.TryGetValue(raw.ToUpperInvariant(), out var mapped) ? mapped : raw.ToLowerInvariant();
            var side = Given(order, "transaction_type", "");
            var symbol = Truthy(order["symbol"]) ? Str(order["symbol"]) : Str(order["trading_symbol"]) ?? "";
            if (Str(side) == "BUY") buy++;
            else if (Str(side) == "SELL") sell++;
            if (status == "complete") completed++;
            else if (status is "open" or "trigger pending") open++;
            else if (status == "rejected") rejected++;
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = Given(order, "exchange", ""), ["action"] = side, ["quantity"] = Given(order, "quantity", 0), ["price"] = Given(order, "price", 0.0), ["trigger_price"] = Given(order, "trigger_price", 0.0),
                ["pricetype"] = PriceTypes.GetValueOrDefault(orderType, orderType), ["product"] = Given(order, "product", ""), ["orderid"] = Given(order, "groww_order_id", ""), ["order_status"] = status == "trigger pending" ? "open" : status,
                ["timestamp"] = Given(order, "created_at", ""),
            });
        }

        return new BrokerBook(rows, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    // ---- trade book ------------------------------------------------------------------------------------------

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (orders, failure) = await ReadOrderListAsync(session, cancellationToken);
        if (failure is not null) throw new BrokerException($"Could not read the {Display} order book: {failure}", 500);
        var rows = new JsonArray();
        var failed = 0;
        foreach (var order in orders)
        {
            if (Num(order["filled_quantity"]) <= 0 || !Truthy(order["groww_order_id"])) continue;
            var id = Str(order["groww_order_id"])!;
            var trades = await TradesOfAsync(session, id, Str(order["segment"]), cancellationToken);
            if (trades is null)
            {
                failed++;
                continue;
            }

            foreach (var trade in trades)
            {
                var quantity = Num(trade["quantity"]);
                var price = Num(trade["price"]);
                rows.Add(new JsonObject
                {
                    ["symbol"] = trade["symbol"]?.DeepClone() ?? "", ["exchange"] = trade["exchange"]?.DeepClone() ?? "", ["product"] = Given(trade, "product", ""), ["action"] = Given(trade, "transaction_type", ""), ["quantity"] = quantity,
                    ["average_price"] = price, ["trade_price"] = price, ["trade_value"] = quantity * price, ["orderid"] = trade["order_id"]?.DeepClone() ?? "", ["timestamp"] = Given(trade, "trade_date_time", ""), ["tradeid"] = Given(trade, "trade_id", ""),
                });
            }
        }

        if (failed > 0) throw new BrokerException($"{Display} did not return the trades for {failed} filled order(s). Try again shortly; the order book shows their fills.", 500);
        return rows;
    }

    /// <summary>All the fills of one order, with OpenAlgo's exchange and symbol; null when no segment would give them.</summary>
    private async Task<List<JsonObject>?> TradesOfAsync(BrokerSession session, string orderId, string? segment, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> segments = segment is "CASH" or "FNO" ? [segment] : await SegmentsOfAsync(session, orderId, cancellationToken);
        foreach (var seg in segments)
        {
            var (list, failure) = await PagedAsync(session, $"/v1/order/trades/{orderId}", seg, "trade_list", TradePage, cancellationToken);
            if (failure is not null) continue;
            var trades = new List<JsonObject>();
            foreach (var trade in list)
            {
                var exchange = OpenAlgoExchange(Str(trade["exchange"]) ?? "", trade.ContainsKey("segment") ? Str(trade["segment"]) : seg);
                var brokerSymbol = Str(trade["trading_symbol"]) ?? "";
                trades.Add(new JsonObject
                {
                    ["trade_id"] = Given(trade, "groww_trade_id", ""), ["order_id"] = Given(trade, "groww_order_id", orderId), ["symbol"] = await OpenAlgoSymbolAsync(brokerSymbol, exchange, cancellationToken) is { Length: > 0 } oa ? oa : brokerSymbol,
                    ["quantity"] = Given(trade, "quantity", 0), ["price"] = Given(trade, "price", 0), ["exchange"] = exchange, ["product"] = Given(trade, "product", ""), ["transaction_type"] = Given(trade, "transaction_type", ""),
                    ["trade_date_time"] = Given(trade, "trade_date_time", ""),
                });
            }

            return trades;
        }

        return null;
    }

    // ---- positions -------------------------------------------------------------------------------------------

    private sealed record PositionRead(List<JsonObject> Rows, bool FnoFailed, bool FnoUnreadable);

    private static string? FnoFailure(int status, JsonNode? body, string text)
    {
        if (Succeeded(status, body) || SaysNoPositions(body, ["error.message", "message"])) return null;
        var shown = body is not null ? body.ToJsonString() : text;
        return $"FNO segment: HTTP {status}, {(shown.Length > 200 ? shown[..200] : shown)}";
    }

    private async Task<JsonObject> PositionRowAsync(JsonObject position, string segment, CancellationToken cancellationToken)
    {
        var brokerSymbol = Str(position["trading_symbol"]) ?? "";
        var brokerExchange = Str(position["exchange"]) ?? "";
        var rowSegment = Truthy(position["segment"]) ? Str(position["segment"])! : segment;
        var exchange = OpenAlgoExchange(brokerExchange, rowSegment);
        var buy = Num(position["credit_quantity"]) + Num(position["carry_forward_credit_quantity"]);
        var sell = Num(position["debit_quantity"]) + Num(position["carry_forward_debit_quantity"]);
        var sold = Num(position["debit_price"]);
        return new JsonObject
        {
            ["symbol"] = await OpenAlgoSymbolAsync(brokerSymbol, exchange, cancellationToken) is { Length: > 0 } oa ? oa : brokerSymbol, ["trading_symbol"] = brokerSymbol, ["exchange"] = exchange, ["brexchange"] = brokerExchange,
            ["segment"] = rowSegment, ["product"] = Given(position, "product", ""), ["quantity"] = Given(position, "quantity", buy - sell), ["average_price"] = Num(position["net_price"]), ["ltp"] = 0.0, ["realised"] = Num(position["realised_pnl"]),
            ["unrealised"] = 0.0, ["pnl"] = Num(position["realised_pnl"]),
        };
    }

    /// <summary>Last traded prices from /v1/live-data/ltp for the symbols of each segment; a batch Groww refuses is left out, so a position shows no price rather than a wrong one.</summary>
    private async Task<Dictionary<string, double>> LivePricesAsync(BrokerSession session, IEnumerable<KeyValuePair<string, List<string>>> bySegment, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, double>();
        foreach (var (segment, wanted) in bySegment)
        {
            var keys = wanted.Distinct().ToList();
            for (var start = 0; start < keys.Count; start += LtpBatch)
            {
                var batch = keys.Skip(start).Take(LtpBatch).ToList();
                try
                {
                    var query = new[] { KeyValuePair.Create("segment", segment), KeyValuePair.Create("exchange_symbols", string.Join(",", batch)) };
                    var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/v1/live-data/ltp", "live", query, null, cancellationToken);
                    if (status != 200 || body is not JsonObject answer || Str(answer["status"]) != "SUCCESS" || answer["payload"] is not JsonObject payload) continue;
                    foreach (var key in batch)
                        if (Num(payload[key]) is var ltp && ltp > 0) prices[key] = ltp;
                }
                catch (BrokerException ex) when (ex.HttpStatus == 502)
                {
                }
            }
        }

        return prices;
    }

    private async Task AttachPricesAsync(BrokerSession session, List<JsonObject> rows, CancellationToken cancellationToken)
    {
        var wanted = new OrderedDictionary<string, List<string>>();
        foreach (var row in rows)
        {
            if (Num(row["quantity"]) == 0 || string.IsNullOrEmpty(Str(row["trading_symbol"]))) continue;
            var segment = Str(row["segment"]) ?? "";
            if (!wanted.TryGetValue(segment, out var list)) wanted[segment] = list = [];
            list.Add($"{Str(row["brexchange"])}_{Str(row["trading_symbol"])}");
        }

        if (wanted.Count == 0) return;
        var prices = await LivePricesAsync(session, wanted, cancellationToken);
        foreach (var row in rows)
        {
            if (!prices.TryGetValue($"{Str(row["brexchange"])}_{Str(row["trading_symbol"])}", out var ltp) || ltp == 0 || Num(row["quantity"]) == 0) continue;
            row["ltp"] = ltp;
            if (Num(row["average_price"]) > 0)
            {
                var unrealised = (ltp - Num(row["average_price"])) * Num(row["quantity"]);
                row["unrealised"] = unrealised;
                row["pnl"] = Num(row["realised"]) + unrealised;
            }
        }
    }

    /// <summary>
    /// The day's positions from both segments. A CASH read that fails is an error; an F&amp;O read that fails is kept apart, as it fails on accounts without F&amp;O.
    /// </summary>
    private async Task<PositionRead> ReadPositionsAsync(BrokerSession session, bool withPrices, CancellationToken cancellationToken)
    {
        var rows = new List<JsonObject>();
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/v1/positions/user", "non_trading", [KeyValuePair.Create("segment", "CASH")], null, cancellationToken);
        string? cashFailure = null;
        if (Succeeded(status, body))
        {
            foreach (var position in (Payload(body)["positions"] as JsonArray ?? []).OfType<JsonObject>()) rows.Add(await PositionRowAsync(position, "CASH", cancellationToken));
        }
        else if (!SaysNoPositions(body))
        {
            cashFailure = status == 200 && body is not JsonObject ? "the answer could not be read" : ReasonOf(body, $"HTTP {status}");
        }

        string? fnoFailure = null;
        var unreadable = false;
        try
        {
            var (fnoStatus, fnoBody, fnoText) = await SendAsync(session, HttpMethod.Get, "/v1/positions/user", "non_trading", [KeyValuePair.Create("segment", "FNO")], null, cancellationToken);
            fnoFailure = FnoFailure(fnoStatus, fnoBody, fnoText);
            unreadable = fnoFailure is not null && fnoStatus is not (>= 400 and < 500);
            if (fnoFailure is null && fnoStatus == 200 && fnoBody is JsonObject fno && Str(fno["status"]) == "SUCCESS")
                foreach (var position in (Payload(fnoBody)["positions"] as JsonArray ?? []).OfType<JsonObject>()) rows.Add(await PositionRowAsync(position, "FNO", cancellationToken));
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            fnoFailure = ex.Message;
            unreadable = true;
        }

        // Both segments are asked for, as OpenAlgo asks, before a CASH one that failed is reported: the position is not known without it.
        if (cashFailure is not null) throw new BrokerException($"{Display} did not give the position book for the cash market: {cashFailure}. Try again in a moment.", 502);
        if (withPrices) await AttachPricesAsync(session, rows, cancellationToken);
        return new PositionRead(rows, fnoFailure is not null, unreadable);
    }

    private static JsonArray PositionRows(IEnumerable<JsonObject> rows) => new(rows.Select(r => (JsonNode?)new JsonObject
    {
        ["symbol"] = r["symbol"]!.DeepClone(), ["exchange"] = r["exchange"]!.DeepClone(), ["product"] = r["product"]?.DeepClone() ?? "", ["quantity"] = r["quantity"]?.DeepClone() ?? 0, ["average_price"] = r["average_price"]!.DeepClone(),
        ["ltp"] = r["ltp"]!.DeepClone(), ["pnl"] = r["pnl"]!.DeepClone(),
    }).ToArray());

    /// <summary>
    /// The book a position decision is made from, with live prices. A CASH segment that cannot be read is an error, and so is an F&amp;O one that Groww could not be asked about or answered with something other than a refusal of the
    /// account (as for one with no F&amp;O, which is skipped). OpenAlgo shows what it could read in the position book; here a half book is never shown as the whole, because a smart order sizes itself from it.
    /// </summary>
    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var read = await ReadPositionsAsync(session, true, cancellationToken);
        if (read.FnoUnreadable) throw new BrokerException($"{Display}'s F&O positions could not be read, so the position is not known. Try again in a moment.", 502);
        return PositionRows(read.Rows);
    }

    // ---- holdings --------------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, body, text) = await SendAsync(session, HttpMethod.Get, "/v1/holdings/user", "non_trading", null, null, cancellationToken);
        if (status != 200) throw new BrokerException($"{Display} did not give the holdings: HTTP {status}. Try again in a moment.", status);
        if (body is not JsonObject answer || Str(answer["status"]) != "SUCCESS") throw new BrokerException($"{Display} did not give the holdings: {ReasonOf(body, "the answer could not be read")}. Try again in a moment.", 502);
        var held = new List<JsonObject>();
        foreach (var holding in (Payload(body)["holdings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var brokerSymbol = Str(holding["trading_symbol"]) ?? "";
            string? exchange = null;
            string? oa = null;
            foreach (var candidate in new[] { "NSE", "BSE" })
                if (await OpenAlgoSymbolAsync(brokerSymbol, candidate, cancellationToken) is { Length: > 0 } found)
                {
                    exchange = candidate;
                    oa = found;
                    break;
                }

            held.Add(new JsonObject { ["symbol"] = oa ?? brokerSymbol, ["trading_symbol"] = brokerSymbol, ["exchange"] = exchange ?? "", ["quantity"] = Num(holding["quantity"]), ["average_price"] = Num(holding["average_price"]) });
        }

        var wanted = new OrderedDictionary<string, List<string>> { ["CASH"] = held.Where(h => Str(h["exchange"]) is { Length: > 0 }).Select(h => $"{Str(h["exchange"])}_{Str(h["trading_symbol"])}").ToList() };
        var prices = await LivePricesAsync(session, wanted, cancellationToken);
        var rows = new JsonArray();
        double invested = 0, holdingValue = 0, profit = 0;
        foreach (var h in held)
        {
            var quantity = Num(h["quantity"]);
            var average = Num(h["average_price"]);
            double ltp = 0, pnl = 0, pnlPercent = 0;
            if (prices.TryGetValue($"{Str(h["exchange"])}_{Str(h["trading_symbol"])}", out var price) && price != 0)
            {
                ltp = price;
                pnl = (ltp - average) * quantity;
                pnlPercent = average != 0 ? (ltp - average) / average * 100 : 0.0;
            }

            invested += quantity * average;
            holdingValue += ltp != 0 ? quantity * ltp : quantity * average;
            profit += pnl;
            var row = new JsonObject
            {
                ["symbol"] = h["symbol"]!.DeepClone(), ["exchange"] = h["exchange"]!.DeepClone(), ["quantity"] = quantity, ["average_price"] = average, ["product"] = "CNC", ["pnl"] = BookFormatting.RoundPy(pnl, 2),
                ["pnlpercent"] = BookFormatting.RoundPy(pnlPercent, 2),
            };
            if (ltp != 0) row["ltp"] = BookFormatting.RoundPy(ltp, 2);
            rows.Add(row);
        }

        var statistics = new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.RoundPy(holdingValue, 2), ["totalinvvalue"] = BookFormatting.RoundPy(invested, 2), ["totalpnlpercentage"] = BookFormatting.RoundPy(invested != 0 ? profit / invested * 100 : 0, 2),
            ["totalprofitandloss"] = BookFormatting.RoundPy(profit, 2),
        };
        return new BrokerBook(rows, statistics);
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/v1/margins/detail/user", "non_trading", null, null, cancellationToken);
        if (status != 200 || body is not JsonObject answer || Str(answer["status"]) != "SUCCESS") return [];
        var data = Payload(body);
        if (data.Count == 0) return [];
        // One figure that is not a number fails the whole answer, as OpenAlgo's format does.
        double Money(string key) => !data.ContainsKey(key) ? 0 : BookFormatting.Number(data[key]) ?? throw new FormatException(key);
        try
        {
            var (clear, collateral, used) = (Money("clear_cash"), Money("collateral_available"), Money("net_margin_used"));
            double realised = 0, unrealised = 0;
            try
            {
                var read = await ReadPositionsAsync(session, true, cancellationToken);
                realised = read.Rows.Sum(r => Num(r["realised"]));
                unrealised = read.Rows.Sum(r => Num(r["unrealised"]));
            }
            catch (BrokerException)
            {
                // The cash figures are still worth showing when the positions cannot be read.
            }

            return new JsonObject
            {
                ["availablecash"] = BookFormatting.Fixed2(clear), ["collateral"] = BookFormatting.Fixed2(collateral), ["m2mrealized"] = BookFormatting.Fixed2(realised), ["m2munrealized"] = BookFormatting.Fixed2(unrealised),
                ["utiliseddebits"] = BookFormatting.Fixed2(used),
            };
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
