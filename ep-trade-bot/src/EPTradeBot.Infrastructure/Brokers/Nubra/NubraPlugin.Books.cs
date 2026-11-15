using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>
/// Nubra's books and funds (broker/nubra api/order_api.py, api/funds.py, mapping/order_data.py). The order book is one read in buckets (open, executed, cancelled, rejected, expired, gtt), the status of an order is its bucket, and the trade book is the orders that filled. A row's
/// instrument is found from its reference id first and its broker symbol second, among the exchanges its segment makes likely; one the master does not have keeps Nubra's own symbol. Prices are in paise.
/// </summary>
public sealed partial class NubraPlugin
{
    private static readonly Dictionary<string, string> BucketStatus = new() { ["open"] = "open", ["executed"] = "complete", ["cancelled"] = "cancelled", ["rejected"] = "rejected", ["expired"] = "cancelled", ["gtt"] = "open" };
    private static readonly Regex Stamp = new(@"^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}:\d{2}:\d{2})(?:\.\d+)?(?:Z|[+-]\d{2}(?::?\d{2})?)?)?$", RegexOptions.CultureInvariant);

    private static string DerivativeExchange(string exchange) => exchange switch { "NSE" => "NFO", "BSE" => "BFO", _ => "" };

    private static string MapExchange(string? brokerExchange, string? derivativeType)
    {
        var exchange = (brokerExchange ?? "").ToUpperInvariant();
        return (derivativeType ?? "").ToUpperInvariant() is "OPT" or "FUT" ? DerivativeExchange(exchange) is { Length: > 0 } d ? d : exchange : exchange;
    }

    private static List<string> Candidates(string? brokerExchange, string? derivativeType)
    {
        var exchange = (brokerExchange ?? "").ToUpperInvariant();
        if (exchange.Length == 0) return [];
        var derivative = DerivativeExchange(exchange);
        var kind = (derivativeType ?? "").ToUpperInvariant();
        if (kind is "OPT" or "FUT") return [derivative.Length > 0 ? derivative : exchange];
        if (kind.Length > 0) return [exchange];
        return derivative.Length > 0 ? [derivative, exchange] : [exchange];
    }

    private static string DerivativeTypeOf(JsonObject? row)
    {
        if (row is null) return "";
        foreach (var candidate in new[] { row, row["refData"] as JsonObject })
            if (candidate is not null && Truthy(candidate["derivativeType"])) return Str(candidate["derivativeType"]) ?? "";
        return row["legs"] is JsonArray { Count: > 0 } legs && legs[0] is JsonObject leg && leg["refData"] is JsonObject data && Truthy(data["derivativeType"]) ? Str(data["derivativeType"]) ?? "" : "";
    }

    private static string BrokerExchangeOf(JsonObject? row)
    {
        if (row is null) return "";
        if (Truthy(row["exchange"])) return Str(row["exchange"]) ?? "";
        var sources = new List<JsonObject?> { row["refData"] as JsonObject };
        if (row["legs"] is JsonArray legs && legs.Count > 0) sources.Add(legs[0] as JsonObject);
        foreach (var source in sources)
        {
            if (source is null) continue;
            if (Truthy(source["exchange"])) return Str(source["exchange"]) ?? "";
            if (source["refData"] is JsonObject inner && Truthy(inner["exchange"])) return Str(inner["exchange"]) ?? "";
        }

        return "";
    }

    private async Task<(string? Symbol, string? Exchange)> ResolveAsync(string? brokerExchange, string? derivativeType, string? refId, string? brokerSymbol, CancellationToken cancellationToken)
    {
        refId = (refId ?? "").Trim();
        brokerSymbol = (brokerSymbol ?? "").Trim();
        foreach (var candidate in Candidates(brokerExchange, derivativeType))
        {
            if (refId.Length > 0 && await symbols.FindByTokenAsync(Name, refId, candidate, cancellationToken) is { } byToken) return (byToken.Symbol, candidate);
            if (brokerSymbol.Length > 0 && await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, candidate, cancellationToken) is { } bySymbol) return (bySymbol.Symbol, candidate);
        }

        return (null, null);
    }

    private static string Reformat(JsonNode? value)
    {
        if (!Truthy(value)) return "";
        var text = (Str(value) ?? value!.ToJsonString()).Trim();
        var m = Stamp.Match(text);
        return m.Success ? $"{m.Groups[1].Value} {(m.Groups[2].Success ? m.Groups[2].Value : "00:00:00")}" : text;
    }

    private static string LastStamp(JsonObject order)
    {
        if (order["timestamps"] is not JsonObject stamps) return "";
        foreach (var key in new[] { "lastUpdatedAt", "filledAt", "sentToColoAt", "intentCreatedAt" })
            if (Truthy(stamps[key])) return Reformat(stamps[key]);
        return "";
    }

    private async Task<(string Symbol, string Exchange, string RefId)> OrderInstrumentAsync(JsonObject order, CancellationToken cancellationToken)
    {
        var refData = order["refData"] as JsonObject ?? [];
        var refId = order["refId"];
        if (!Truthy(refId) && order["legs"] is JsonArray { Count: > 0 } legs)
        {
            var leg = legs[0] as JsonObject ?? [];
            refData = leg["refData"] as JsonObject is { Count: > 0 } r ? r : refData;
            refId = leg["refId"];
        }

        var brokerExchange = Truthy(refData["exchange"]) ? Str(refData["exchange"]) : Str(order["exchange"]) ?? "";
        var derivative = Truthy(refData["derivativeType"]) ? Str(refData["derivativeType"]) : DerivativeTypeOf(order);
        var id = Truthy(refId) ? Text(refId) : "";
        var stockName = Str(refData["stockName"]) ?? "";
        var (symbol, exchange) = await ResolveAsync(brokerExchange, derivative, id, stockName, cancellationToken);
        if (symbol is not null) return (symbol, exchange!, id ?? "");
        return (stockName.Length > 0 ? stockName : Str(refData["displayName"]) ?? "", MapExchange(brokerExchange, derivative), id ?? "");
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : Str(node);

    private static List<(string Bucket, JsonObject Order)> Flatten(JsonObject answer, IReadOnlySet<string>? only = null)
    {
        var rows = new List<(string, JsonObject)>();
        if (answer["orders"] is not JsonObject grouped) return rows;
        foreach (var (bucket, orders) in grouped)
        {
            if (only is not null && !only.Contains(bucket)) continue;
            if (orders is JsonArray list)
                foreach (var order in list.OfType<JsonObject>()) rows.Add((bucket, order));
        }

        return rows;
    }

    private static long Trigger(JsonObject order)
    {
        var config = order["entryConfig"] is JsonArray ca ? ca.FirstOrDefault() : order["entryConfig"];
        if (config is not JsonObject c) return 0;
        var triggers = c["triggers"] is JsonArray ta ? ta.FirstOrDefault() : c["triggers"];
        if (triggers is not JsonObject t || t["ltp"] is not JsonObject ltp) return 0;
        foreach (var bound in new[] { "atOrAbove", "atOrBelow" })
            if (ltp[bound] is JsonObject node && Truthy(node["value"])) return (long)Math.Truncate(Float(node["value"]));
        return 0;
    }

    private static JsonNode? Raw(JsonObject row, string key) => Truthy(row[key]) ? row[key]!.DeepClone() : 0;

    private static double Rupees(JsonNode? paise) => Truthy(paise) ? Float(paise) / 100 : 0.0;

    // ---- order book ------------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var answer = await ReadAsync(session, "/sentinel/orders", "order book", true, cancellationToken);
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        var rows = new JsonArray();
        foreach (var (bucket, order) in Flatten(answer))
        {
            var (symbol, exchange, _) = await OrderInstrumentAsync(order, cancellationToken);
            var status = BucketStatus.TryGetValue(bucket, out var mapped) ? mapped : (Str(order["status"]) ?? "").ToLowerInvariant();
            var trigger = Trigger(order);
            var priceType = (Str(order["priceType"]) ?? "").ToUpperInvariant();
            var type = trigger != 0 ? priceType == "MARKET" ? "SL-M" : "SL" : priceType is "MARKET" or "LIMIT" ? priceType : "MARKET";
            if (type is "SL" or "SL-M" && status == "open") status = "trigger pending";
            var side = (Str(order["side"]) ?? "").ToUpperInvariant();
            if (side == "BUY") buy++;
            else if (side == "SELL") sell++;
            if (status == "complete") completed++;
            else if (status == "open") open++;
            else if (status == "rejected") rejected++;
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = side, ["quantity"] = Raw(order, "orderQty"), ["price"] = Rupees(order["orderPrice"]), ["trigger_price"] = trigger != 0 ? trigger / 100.0 : 0.0, ["pricetype"] = type,
                ["product"] = ProductOf(Str(order["deliveryType"])), ["orderid"] = Truthy(order["intentOrderId"]) ? Text(order["intentOrderId"]) : "", ["order_status"] = status, ["timestamp"] = LastStamp(order),
            });
        }

        return new BrokerBook(rows, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var answer = await ReadAsync(session, "/sentinel/orders", "trade book", true, cancellationToken);
        var rows = new JsonArray();
        foreach (var (_, order) in Flatten(answer))
        {
            var filled = Truthy(order["filledQty"]) ? order["filledQty"]!.DeepClone() : 0;
            var quantity = Float(filled);
            if (quantity <= 0) continue;
            var (symbol, exchange, _) = await OrderInstrumentAsync(order, cancellationToken);
            var price = Rupees(order["filledPrice"]);
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = ProductOf(Str(order["deliveryType"])), ["action"] = (Str(order["side"]) ?? "").ToUpperInvariant(), ["quantity"] = filled,
                ["average_price"] = BookFormatting.RoundPy(price, 2), ["trade_value"] = BookFormatting.RoundPy(BookFormatting.RoundPy(price, 2) is var _ ? price * quantity : 0, 2),
                ["orderid"] = Truthy(order["intentOrderId"]) ? Text(order["intentOrderId"]) : "", ["timestamp"] = LastStamp(order),
            });
        }

        return rows;
    }

    // ---- positions -------------------------------------------------------------------------------------------

    private static List<JsonObject> PositionsOf(JsonObject answer) => answer["portfolio"] is JsonObject portfolio && portfolio["positions"] is JsonArray list ? list.OfType<JsonObject>().ToList() : [];

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var answer = await ReadAsync(session, "/sentinel/portfolio/positions", "position book", true, cancellationToken);
        var rows = new JsonArray();
        foreach (var position in PositionsOf(answer))
        {
            var net = (long)Math.Truncate(Float(Truthy(position["netQty"]) ? position["netQty"] : position["netQuantity"]));
            var brokerExchange = BrokerExchangeOf(position);
            var derivative = DerivativeTypeOf(position);
            var brokerSymbol = Str(position["symbol"]) ?? "";
            var (symbol, exchange) = await ResolveAsync(brokerExchange, derivative, Truthy(position["refId"]) ? Text(position["refId"]) : "", brokerSymbol, cancellationToken);
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol ?? brokerSymbol, ["exchange"] = exchange ?? MapExchange(brokerExchange, derivative), ["product"] = ProductOf(Str(position["deliveryType"])), ["quantity"] = net,
                ["average_price"] = Rupees(position["avgPrice"]), ["ltp"] = Rupees(position["ltp"] is not null ? position["ltp"] : position["lastTradedPrice"]), ["pnl"] = Truthy(position["pnl"]) ? Float(position["pnl"]) / 100 : 0.0,
            });
        }

        return rows;
    }

    // ---- holdings --------------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var answer = await ReadAsync(session, "/sentinel/portfolio/holdings", "holdings", true, cancellationToken);
        var rows = new JsonArray();
        if (answer["portfolio"] is not JsonObject portfolio || !portfolio.ContainsKey("holdings")) return new BrokerBook(rows, Totals(null));
        foreach (var holding in (portfolio["holdings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var exchange = holding.ContainsKey("exchange") && holding["exchange"] is not null ? Str(holding["exchange"]) ?? "" : "NSE";
            var brokerSymbol = Str(holding["symbol"]) ?? "";
            var oa = string.IsNullOrEmpty(brokerSymbol) ? null : (await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken))?.Symbol;
            double Money(string key) => Math.Round(Rupees(holding[key]), 2, MidpointRounding.ToEven) is var _ ? BookFormatting.RoundPy(Rupees(holding[key]), 2) : 0;
            rows.Add(new JsonObject
            {
                ["symbol"] = string.IsNullOrEmpty(oa) ? brokerSymbol : oa, ["exchange"] = exchange, ["quantity"] = Raw(holding, "quantity"), ["product"] = "CNC", ["average_price"] = Money("avgPrice"), ["ltp"] = Money("lastTradedPrice"),
                ["pnl"] = Money("netPnl"), ["pnlpercent"] = BookFormatting.RoundPy(Truthy(holding["netPnlChg"]) ? Float(holding["netPnlChg"]) : 0, 2), ["invested_value"] = Money("investedValue"), ["current_value"] = Money("currentValue"),
                ["day_pnl"] = Money("dayPnl"), ["day_pnl_chg"] = Money("dayPnl"), ["ltp_chg"] = BookFormatting.RoundPy(Truthy(holding["netPnlChg"]) ? Float(holding["netPnlChg"]) : 0, 2), ["ref_id"] = Truthy(holding["refId"]) ? Text(holding["refId"]) : "",
            });
        }

        return new BrokerBook(rows, Totals(portfolio["holdingStats"] as JsonObject));
    }

    private static JsonObject Totals(JsonObject? stats)
    {
        if (stats is null || stats.Count == 0) return new JsonObject { ["totalholdingvalue"] = 0, ["totalinvvalue"] = 0, ["totalprofitandloss"] = 0, ["totalpnlpercentage"] = 0 };
        double Money(string key) => BookFormatting.RoundPy(Rupees(stats[key]), 2);
        return new JsonObject
        {
            ["totalholdingvalue"] = Money("currentValue"), ["totalinvvalue"] = Money("investedAmount"), ["totalprofitandloss"] = Money("totalPnl"), ["totalpnlpercentage"] = BookFormatting.RoundPy(Truthy(stats["totalPnlChg"]) ? Float(stats["totalPnlChg"]) : 0, 2),
        };
    }

    // ---- funds -----------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var answer = await ReadAsync(session, "/sentinel/portfolio/user_funds_and_margin", "funds", false, cancellationToken);
        if (answer["portFundsAndMargin"] is not JsonObject data || data.Count == 0) return [];
        // One value that is not a number zeroes the whole row, as OpenAlgo's does.
        double Money(string key) => data[key] is null || !Truthy(data[key]) ? 0.0 : (BookFormatting.Number(data[key]) ?? (double.TryParse(Str(data[key]), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new FormatException("not a number"))) / 100;
        try
        {
            var unrealized = Money("mtmEqIdayCnc") + Money("mtmEqDelivery") + Money("mtmDeriv");
            return new JsonObject
            {
                ["availablecash"] = BookFormatting.Fixed2(Money("netMarginAvailable")), ["collateral"] = BookFormatting.Fixed2(Money("totalCollateral")), ["m2mrealized"] = BookFormatting.Fixed2(Money("netDerivativePrem")),
                ["m2munrealized"] = BookFormatting.Fixed2(unrealized), ["utiliseddebits"] = BookFormatting.Fixed2(Money("totalMarginBlocked")),
            };
        }
        catch (FormatException)
        {
            return new JsonObject { ["availablecash"] = "0.00", ["collateral"] = "0.00", ["m2mrealized"] = "0.00", ["m2munrealized"] = "0.00", ["utiliseddebits"] = "0.00" };
        }
    }
}
