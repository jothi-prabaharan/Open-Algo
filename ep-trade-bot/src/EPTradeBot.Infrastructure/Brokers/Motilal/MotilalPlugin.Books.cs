using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

/// <summary>Motilal's books (mapping/order_data.py): the order, trade and position books and holdings, and the funds the margin detail report gives.</summary>
public sealed partial class MotilalPlugin
{
    private const int DefaultPrecision = 2;

    /// <summary>The rows of a book. A failed read is an error that says why, where OpenAlgo shows it as an empty book; an answer with no data is an empty book.</summary>
    private async Task<List<JsonObject>> ReadBookAsync(BrokerSession session, string endpoint, JsonNode? payload, string what, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(session, endpoint, payload, cancellationToken);
        if (!reply.Ok) throw new BrokerException(reply.Body is null ? $"{Display} could not send the {what}: {reply.Failure}" : reply.Failure!, reply.Status >= 400 ? reply.Status : 500);
        return (reply.Body!["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }

    private async Task<string?> SymbolByTokenAsync(string token, string exchange, CancellationToken cancellationToken) =>
        token.Length > 0 ? (await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken))?.Symbol : null;

    /// <summary>The price scale of a row (a price is its value times 10 to the precision). Null means the endpoint publishes none and prices pass through as they are.</summary>
    private static int? PrecisionOf(JsonObject row, int? fallback)
    {
        if (!row.TryGetPropertyValue("precision", out var node) || node is null) return fallback;
        var precision = (int)Int(node);
        return precision < 0 || precision > 8 ? DefaultPrecision : precision;
    }

    private static double Scale(JsonNode? node, int? precision)
    {
        var value = Float(node);
        return precision is null ? value : value / Math.Pow(10, precision.Value);
    }

    private static string StatusOf(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "traded" or "complete" => "complete",
        "sent" or "confirm" or "open" or "partial" or "unknown" => "open",
        "rejected" or "error" => "rejected",
        "cancel" or "cancelled" => "cancelled",
        _ => "open",
    };

    private static string TimestampOf(JsonObject order)
    {
        foreach (var field in new[] { "lastmodifiedtime", "entrydatetime", "recordinserttime" })
        {
            if (order[field] is null) continue;
            var text = (Str(order[field]) ?? "").Trim();
            if (text.Length > 0 && text != "0") return text;
        }

        return "";
    }

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var result = new JsonArray();
        foreach (var order in await ReadBookAsync(session, "getorderbook", null, "order book", cancellationToken))
        {
            var exchange = OpenAlgoExchange(Str(order["exchange"]));
            var symbol = await SymbolByTokenAsync(Str(order["symboltoken"]) ?? "", exchange, cancellationToken) ?? Str(order["symbol"]) ?? "";
            var precision = PrecisionOf(order, null);
            var trigger = Scale(order["triggerprice"], precision);
            var average = Scale(order["averageprice"], precision);
            var price = Scale(order["price"], precision);
            var type = (Str(order["ordertype"]) ?? "").Trim().ToUpperInvariant();
            if (type == "STOPLOSS") type = trigger > 0 ? "SL" : "SL-M";
            var status = StatusOf(Str(order["orderstatus"]));
            var side = (Str(order["buyorsell"]) ?? "").Trim().ToUpperInvariant();
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = side, ["quantity"] = Int(order["orderqty"]), ["price"] = BookFormatting.RoundPy(average > 0 ? average : price, 2),
                ["trigger_price"] = BookFormatting.RoundPy(trigger, 2), ["pricetype"] = type, ["product"] = ProductFrom(Str(order["producttype"])), ["orderid"] = Str(order["uniqueorderid"]) ?? "",
                ["order_status"] = status, ["timestamp"] = TimestampOf(order),
            });
        }

        return new BrokerBook(result, new JsonObject
        {
            ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected,
        });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var trade in await ReadBookAsync(session, "gettradebook", null, "trade book", cancellationToken))
        {
            var exchange = OpenAlgoExchange(Str(trade["exchange"]));
            var symbol = await SymbolByTokenAsync(Str(trade["symboltoken"]) ?? "", exchange, cancellationToken) ?? Str(trade["symbol"]) ?? "";
            // A row with no usable scale (absent, or 0) is read at the documented two places, never as paisa.
            var precision = PrecisionOf(trade, DefaultPrecision);
            if (precision is null or <= 0) precision = DefaultPrecision;
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = ProductFrom(Str(trade["producttype"])), ["action"] = (Str(trade["buyorsell"]) ?? "").ToUpperInvariant(), ["quantity"] = Int(trade["tradeqty"]),
                ["average_price"] = BookFormatting.RoundPy(Scale(trade["tradeprice"], precision), 2), ["trade_value"] = BookFormatting.RoundPy(Scale(trade["tradevalue"], precision), 2),
                ["orderid"] = Str(trade["uniqueorderid"]) ?? "", ["timestamp"] = Str(trade["tradetime"]) ?? "",
            });
        }

        return result;
    }

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var p in await ReadBookAsync(session, "getposition", null, "positions", cancellationToken))
        {
            var exchange = OpenAlgoExchange(Str(p["exchange"]));
            var symbol = await SymbolByTokenAsync(Str(p["symboltoken"]) ?? "", exchange, cancellationToken) ?? Str(p["symbol"]) ?? "";
            var buyQty = Int(p["buyquantity"]);
            var sellQty = Int(p["sellquantity"]);
            var net = buyQty - sellQty;
            var average = 0.0;
            if (net > 0) average = buyQty > 0 ? Float(p["buyamount"]) / buyQty : 0.0;
            else if (net < 0) average = sellQty > 0 ? Float(p["sellamount"]) / sellQty : 0.0;
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = ProductFrom(Str(p["productname"])), ["quantity"] = net, ["average_price"] = average, ["ltp"] = Float(p["LTP"]),
                ["pnl"] = Float(p["marktomarket"]) + Float(p["bookedprofitloss"]),
            });
        }

        return result;
    }

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var holdings = new JsonArray();
        double invested = 0, value = 0;
        foreach (var h in await ReadBookAsync(session, "getdpholding", new JsonObject(), "holdings", cancellationToken))
        {
            string exchange;
            string symbol;
            if (Int(h["nsesymboltoken"]) > 0)
            {
                exchange = "NSE";
                symbol = await SymbolByTokenAsync(Str(h["nsesymboltoken"]) ?? "", exchange, cancellationToken) ?? Str(h["scripname"]) ?? "";
            }
            else if (Int(h["bsescripcode"]) > 0)
            {
                exchange = "BSE";
                symbol = await SymbolByTokenAsync(Str(h["bsescripcode"]) ?? "", exchange, cancellationToken) ?? Str(h["scripname"]) ?? "";
            }
            else
            {
                exchange = "NSE";
                symbol = Str(h["scripname"]) ?? "";
            }

            var quantity = Int(h["dpquantity"]);
            var average = Float(h["buyavgprice"]);
            var ltp = Float(h["LTP"]);
            double pnl = 0, percent = 0;
            if (ltp > 0 && average > 0 && quantity != 0)
            {
                pnl = (ltp - average) * quantity;
                percent = (ltp - average) / average * 100;
            }

            holdings.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["quantity"] = quantity, ["product"] = "CNC", ["average_price"] = BookFormatting.RoundPy(average, 2), ["pnl"] = BookFormatting.RoundPy(pnl, 2),
                ["pnlpercent"] = BookFormatting.RoundPy(percent, 2),
            });
            var cost = quantity * average;
            invested += cost;
            value += ltp > 0 ? quantity * ltp : cost;
        }

        var profit = value - invested;
        return new BrokerBook(holdings, new JsonObject
        {
            ["totalholdingvalue"] = BookFormatting.RoundPy(value, 2), ["totalinvvalue"] = BookFormatting.RoundPy(invested, 2), ["totalprofitandloss"] = BookFormatting.RoundPy(profit, 2),
            ["totalpnlpercentage"] = BookFormatting.RoundPy(invested != 0 ? profit / invested * 100 : 0.0, 2),
        });
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    private static bool IsRow(JsonObject row, int number) => row["srno"] is JsonValue v && BookFormatting.Number(v) is { } n && n == number;

    private static double? FirstRow(List<JsonObject> rows, int number)
    {
        foreach (var row in rows)
            if (IsRow(row, number)) return Float(row["amount"]);
        return null;
    }

    private static (double Total, int Matched) SumRows(List<JsonObject> rows, int[] numbers)
    {
        double total = 0;
        var matched = 0;
        foreach (var row in rows)
            if (numbers.Any(n => IsRow(row, n)))
            {
                total += Float(row["amount"]);
                matched++;
            }

        return (total, matched);
    }

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(session, "getreportmargindetail", new JsonObject(), cancellationToken);
        if (!reply.Ok || reply.Body?["status"] is null || reply.Body["data"] is not JsonArray { Count: > 0 } data)
            throw new BrokerException($"{Display} could not send the funds: {reply.Failure ?? (Truthy(reply.Body?["message"]) ? Str(reply.Body!["message"]) : "no reason was given.")}", 500);
        var rows = data.OfType<JsonObject>().ToList();
        var collateral = FirstRow(rows, 220) ?? 0.0;
        var used = FirstRow(rows, 300);
        if (used is null)
        {
            var (sum, matched) = SumRows(rows, [301, 321, 340, 360, 380, 381]);
            used = matched > 0 ? sum : 0.0;
        }

        var cash = FirstRow(rows, 201);
        if (cash is null)
        {
            var net = FirstRow(rows, 102);
            if (net is null)
            {
                cash = 0.0;
            }
            else
            {
                var mtm = FirstRow(rows, 600) ?? FirstRow(rows, 400) ?? 0.0;
                cash = net - collateral + used - mtm;
            }
        }

        var (unrealised, unrealisedRows) = SumRows(rows, [402, 422, 442, 462, 482]);
        var (realised, realisedRows) = SumRows(rows, [403, 423, 443, 463, 483]);
        if (unrealisedRows == 0 && realisedRows == 0 && (FirstRow(rows, 600) ?? FirstRow(rows, 400)) is { } combined) unrealised = combined;
        return new JsonObject
        {
            ["availablecash"] = Money(cash.Value), ["collateral"] = Money(collateral), ["m2mrealized"] = Money(realised), ["m2munrealized"] = Money(unrealised), ["utiliseddebits"] = Money(used.Value),
        };
    }
}
