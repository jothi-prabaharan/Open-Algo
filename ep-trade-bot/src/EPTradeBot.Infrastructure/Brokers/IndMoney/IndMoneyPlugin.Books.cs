using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IndMoney;

/// <summary>
/// IndMoney's books and funds (broker/indmoney api/order_api.py, api/funds.py, mapping/order_data.py). The position book is four queries (derivative: margin and intraday; equity: cnc and intraday), whose rows are joined and priced with one batched last price call; the trade book is
/// two (equity, derivative) whose rows are given the side, product and exchange of the order they belong to. IndMoney names the segment and the exchange apart (NSE and EQUITY, NSE and DERIVATIVE), so the exchange of a row is worked out from both and, when they do not say, from which exchange's master holds its token.
/// </summary>
public sealed partial class IndMoneyPlugin
{
    private static readonly HashSet<string> Completed = ["SUCCESS", "TRADED", "COMPLETE", "EXECUTED"];
    private static readonly HashSet<string> OpenStatuses = ["QUEUED", "O-PENDING", "PENDING", "PROCESSING", "INITIATED", "MODIFIED", "PARTIALLY FILLED", "PARTIALLY EXECUTED"];
    private static readonly HashSet<string> Rejected = ["REJECTED", "FAILED", "ABORTED"];
    private static readonly HashSet<string> Cancelled = ["CANCELLED", "EXPIRED", "PARTIALLY FILLED - CANCELLED", "PARTIALLY FILLED - EXPIRED"];
    private static readonly Dictionary<string, string> TypeMap = new() { ["MARKET"] = "MARKET", ["LIMIT"] = "LIMIT", ["STOP_LOSS"] = "SL", ["STOP_LOSS_MARKET"] = "SL-M", ["TRIGGER"] = "SL", ["GTT_LIMIT"] = "SL", ["GTT_MARKET"] = "SL-M", ["OCO"] = "OCO" };
    private static readonly Dictionary<string, string> LegacySegments = new() { ["NSE_EQ"] = "NSE", ["BSE_EQ"] = "BSE", ["NSE_FNO"] = "NFO", ["NSE_FO"] = "NFO", ["BSE_FNO"] = "BFO", ["BSE_FO"] = "BFO" };
    private static readonly Dictionary<(string, string), string> BySegment = new() { [("NSE", "EQUITY")] = "NSE", [("BSE", "EQUITY")] = "BSE", [("NSE", "DERIVATIVE")] = "NFO", [("BSE", "DERIVATIVE")] = "BFO" };

    private async Task<string> ResolveExchangeAsync(string? token, string? exchange, string? segment, CancellationToken cancellationToken)
    {
        var exch = (exchange ?? "").Trim().ToUpperInvariant();
        var seg = (segment ?? "").Trim().ToUpperInvariant();
        if (LegacySegments.TryGetValue(exch, out var legacy)) return legacy;
        if (BySegment.TryGetValue((exch, seg), out var found)) return found;
        var id = (token ?? "").Trim();
        if (id.Length > 0)
        {
            var candidates = seg switch { "DERIVATIVE" => new[] { "NFO", "BFO" }, "EQUITY" => ["NSE", "BSE"], _ => ["NFO", "BFO", "NSE", "BSE"] };
            if (exch is "NSE" or "BSE") candidates = candidates.Where(c => c == exch || c == (exch == "NSE" ? "NFO" : "BFO")).ToArray();
            foreach (var candidate in candidates)
                if (await symbols.FindByTokenAsync(Name, id, candidate, cancellationToken) is not null) return candidate;
        }

        return "NSE";
    }

    private static string MapProduct(string? product, string exchange)
    {
        var p = (product ?? "").Trim().ToUpperInvariant();
        if (p == "INTRADAY") return "MIS";
        if (p is "CNC" or "DELIVERY") return "CNC";
        if (p == "MARGIN") return "NRML";
        return exchange.ToUpperInvariant() is "NFO" or "BFO" or "CDS" or "BCD" or "MCX" ? "NRML" : "MIS";
    }

    private static string Normalize(string? status)
    {
        var s = (status ?? "").ToUpperInvariant().Trim();
        if (Completed.Contains(s)) return "complete";
        if (s == "SL-PENDING") return "trigger pending";
        if (OpenStatuses.Contains(s)) return "open";
        if (Rejected.Contains(s)) return "rejected";
        if (Cancelled.Contains(s)) return "cancelled";
        return s.ToLowerInvariant();
    }

    /// <summary>The first value that reads as a price other than zero, else zero: IndMoney leaves a price that does not apply as an empty string.</summary>
    private static double FirstPrice(params JsonNode?[] values)
    {
        foreach (var value in values)
        {
            if (value is null) continue;
            var text = Str(value);
            if (text is null or "" or "0") continue;
            if (BookFormatting.Number(value) is { } n)
            {
                if (n != 0) return n;
                continue;
            }

            if (double.TryParse(text.Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed != 0) return parsed;
        }

        return 0;
    }

    private static double Float(JsonNode? node, double fallback = 0)
    {
        if (node is null) return fallback;
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s.Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>float(x) of a value that must be a number: absent, an empty string and the text None are zero for IndMoney's own fields, anything else that does not read is an error.</summary>
    private static double StrictFloat(JsonNode? node, string what)
    {
        if (node is null || !Truthy(node)) return 0;
        if (BookFormatting.Number(node) is { } n) return n;
        if (Str(node) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new BrokerException($"{Display} sent {what} that could not be read as a number.", 500);
    }

    private async Task<List<JsonObject>> ReadRowsAsync(BrokerSession session, string path, IEnumerable<KeyValuePair<string, string>>? query, string what, CancellationToken cancellationToken)
    {
        var (data, error) = await ReadAsync(session, path, query, cancellationToken);
        if (error is not null) throw new BrokerException($"{Display} could not send the {what}: {error}.", 500);
        return (data as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }

    private async Task<string> SymbolOfAsync(string? token, string exchange, string fallback, CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(token) && await symbols.FindByTokenAsync(Name, token, exchange, cancellationToken) is { } found ? found.Symbol : fallback;

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = new JsonArray();
        int buy = 0, sell = 0, completed = 0, open = 0, rejected = 0;
        foreach (var r in await ReadRowsAsync(session, "/order-book", null, "order book", cancellationToken))
        {
            var token = Str(r["security_id"]);
            var exchange = await ResolveExchangeAsync(token, Str(r["exchange"]), Str(r["segment"]), cancellationToken);
            var symbol = await SymbolOfAsync(token, exchange, Str(r["name"]) ?? "", cancellationToken);
            var action = (Str(r["txn_type"]) ?? "").ToUpperInvariant();
            var status = Normalize(Str(r["status"]));
            var type = (Str(r["order_type"]) ?? "").ToUpperInvariant();
            if (action == "BUY") buy++;
            else if (action == "SELL") sell++;
            if (status == "complete") completed++;
            else if (status is "open" or "trigger pending") open++;
            else if (status == "rejected") rejected++;
            rows.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["action"] = action, ["quantity"] = r["requested_qty"]?.DeepClone() ?? 0, ["price"] = FirstPrice(r["requested_price"], r["tgt_limit_price"], r["sl_limit_price"]),
                ["trigger_price"] = FirstPrice(r["sl_trigger_price"], r["tgt_trigger_price"]), ["pricetype"] = TypeMap.GetValueOrDefault(type, type), ["product"] = MapProduct(Str(r["product"]), exchange),
                ["orderid"] = Str(r["id"]) ?? "", ["order_status"] = status, ["timestamp"] = Str(r["created_at"]) ?? "",
            });
        }

        return new BrokerBook(rows, new JsonObject { ["total_buy_orders"] = buy, ["total_sell_orders"] = sell, ["total_completed_orders"] = completed, ["total_open_orders"] = open, ["total_rejected_orders"] = rejected });
    }

    public async Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var trades = new List<(JsonObject Row, string Segment)>();
        BrokerException? failed = null;
        foreach (var segment in new[] { "EQUITY", "DERIVATIVE" })
        {
            // Both queries are made, as OpenAlgo makes them; a failure of either fails the read once they have been.
            try
            {
                foreach (var row in await ReadRowsAsync(session, "/trade-book", [new("segment", segment)], "trade book", cancellationToken)) trades.Add((row, segment));
            }
            catch (BrokerException ex)
            {
                failed ??= ex;
            }
        }

        // The order book is asked for too, as OpenAlgo asks for it, before a trade query that failed fails the read.
        var orders = await ReadRowsAsync(session, "/order-book", null, "order book", cancellationToken);
        if (failed is not null) throw failed;

        var byOrder = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var order in orders)
            foreach (var key in new[] { Str(order["exch_order_id"]), Str(order["id"]) })
                if (!string.IsNullOrEmpty(key)) byOrder[key] = order;

        var result = new JsonArray();
        foreach (var (t, segment) in trades)
        {
            var info = Truthy(t["exch_order_id"]) && byOrder.TryGetValue(Str(t["exch_order_id"])!, out var found) ? found : null;
            var exchangeHint = info is not null && Truthy(info["exchange"]) ? Str(info["exchange"]) : Str(t["exchange"]);
            var token = Str(t["scrip_code"]);
            var exchange = await ResolveExchangeAsync(token, exchangeHint, segment, cancellationToken);
            var symbol = string.IsNullOrEmpty(token) ? "" : await SymbolOfAsync(token, exchange, token, cancellationToken);
            var quantity = t["quantity"] is { } q ? q.DeepClone() : 0;
            var price = Truthy(t["price"]) ? StrictFloat(t["price"], "a trade price") : 0.0;
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = MapProduct(info is null ? "" : Str(info["product"]), exchange), ["action"] = info is null ? "" : (Str(info["txn_type"]) ?? "").ToUpperInvariant(),
                ["quantity"] = quantity, ["average_price"] = price, ["trade_value"] = StrictFloat(t["quantity"], "a trade quantity") * price, ["orderid"] = Str(t["exch_order_id"]) ?? "", ["timestamp"] = Str(t["trade_date"]) ?? "",
            });
        }

        return result;
    }

    // ---- positions -------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> QuoteSegments = new() { ["NSE"] = "NSE", ["BSE"] = "BSE", ["NFO"] = "NFO", ["BFO"] = "BFO" };

    public async Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var positions = new List<(JsonObject Row, string Segment, string Product)>();
        BrokerException? failed = null;
        foreach (var (segment, product) in new[] { ("derivative", "margin"), ("derivative", "intraday"), ("equity", "cnc"), ("equity", "intraday") })
        {
            var (data, error) = await ReadAsync(session, "/portfolio/positions", [new("segment", segment), new("product", product)], cancellationToken);
            if (error is not null)
            {
                // All four queries are made, as OpenAlgo makes them; a failure of any fails the read once they have been, because a missing query reads as a flat book.
                failed ??= new BrokerException($"{Display} could not send the {segment} {product} positions: {error}.", 500);
                continue;
            }

            var batch = new List<JsonNode?>();
            if (data is JsonArray list) batch.AddRange(list);
            else if (data is JsonObject o)
            {
                if (o["net_positions"] is JsonArray net) batch.AddRange(net);
                if (o["day_positions"] is JsonArray day) batch.AddRange(day);
            }

            foreach (var row in batch.OfType<JsonObject>()) positions.Add((row, segment, product));
        }

        if (failed is not null) throw failed;

        // One batched call for the last prices of the positions that are open.
        var scripOf = new Dictionary<JsonObject, string>();
        foreach (var (row, segment, _) in positions)
        {
            if (BookFormatting.Number(row["net_qty"] ?? row["net_quantity"]) is { } nq && (long)nq == 0) continue;
            var token = (Str(row["security_id"]) ?? "").Trim();
            if (token.Length == 0) continue;
            var exchange = await ResolveExchangeAsync(token, Truthy(row["exchange"]) ? Str(row["exchange"]) : Str(row["exchange_segment"]), Truthy(row["segment"]) ? Str(row["segment"]) : segment.ToUpperInvariant(), cancellationToken);
            if (QuoteSegments.TryGetValue(exchange, out var api)) scripOf[row] = $"{api}_{token}";
        }

        var prices = new Dictionary<string, double>();
        if (scripOf.Count > 0)
        {
            var codes = string.Join(',', scripOf.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal));
            var (data, error) = await ReadAsync(session, "/market/quotes/ltp", [new("scrip-codes", codes)], cancellationToken);
            if (error is null)
            {
                var quotes = data as JsonObject;
                if (quotes?["data"] is JsonObject inner) quotes = inner;
                if (quotes is not null)
                    foreach (var (key, value) in quotes)
                        if (value is JsonObject q && q["live_price"] is { } live && BookFormatting.Number(live) is { } price) prices[key] = price;
            }
        }

        var result = new JsonArray();
        foreach (var (row, segment, product) in positions)
        {
            var token = Str(row["security_id"]);
            var exchange = await ResolveExchangeAsync(token, Truthy(row["exchange"]) ? Str(row["exchange"]) : Str(row["exchange_segment"]), Truthy(row["segment"]) ? Str(row["segment"]) : segment.ToUpperInvariant(), cancellationToken);
            var name = Truthy(row["trading_symbol"]) ? Str(row["trading_symbol"]) : Str(row["symbol"]) ?? "";
            var symbol = !string.IsNullOrEmpty(token) ? await SymbolOfAsync(token, exchange, name ?? "", cancellationToken) : name ?? "";
            var netQty = row.ContainsKey("net_quantity") ? row["net_quantity"] : row["net_qty"];
            var net = Float(netQty);
            var average = Float(row.ContainsKey("average_price") ? row["average_price"] : row["avg_price"]);
            var live = scripOf.TryGetValue(row, out var scrip) && prices.TryGetValue(scrip, out var p) ? p : (double?)null;
            var ltp = live ?? (Truthy(row["last_traded_price"]) ? Float(row["last_traded_price"]) : Truthy(row["ltp"]) ? Float(row["ltp"]) : Truthy(row["current_price"]) ? Float(row["current_price"]) : Truthy(row["market_price"]) ? Float(row["market_price"]) : 0);
            var multiplier = row.ContainsKey("multiplier") ? Float(row["multiplier"], 1) : 1;
            double pnl;
            if (row["pnl_absolute"] is { } pa) pnl = Float(pa);
            else if (row["pnl"] is { } pp) pnl = Float(pp);
            else if (row["unrealized_profit"] is { } up) pnl = Float(up);
            else pnl = Float(row["realized_profit"]) + (net != 0 && ltp != 0 ? (ltp - average) * net * multiplier : 0);
            result.Add(new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["product"] = product switch { "intraday" => "MIS", "cnc" => "CNC", _ => "NRML" }, ["quantity"] = netQty?.DeepClone() ?? 0, ["average_price"] = average, ["ltp"] = ltp, ["pnl"] = pnl,
            });
        }

        return result;
    }

    // ---- holdings and funds ------------------------------------------------------------------------------------

    public async Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var rows = new JsonArray();
        double value = 0, investment = 0;
        foreach (var h in await ReadRowsAsync(session, "/portfolio/holdings", null, "holdings", cancellationToken))
        {
            var token = Str(h["security_id"]);
            var quantity = h["total_qty"] is { } tq ? tq.DeepClone() : 0;
            var qty = Float(h["total_qty"]);
            var avg = Float(h["avg_price"]);
            investment += qty * avg;
            value += qty * avg;
            rows.Add(new JsonObject
            {
                ["symbol"] = await SymbolOfAsync(token, "NSE", Str(h["symbol"]) ?? "", cancellationToken), ["exchange"] = "NSE", ["quantity"] = quantity, ["product"] = "CNC", ["pnl"] = 0.0, ["pnlpercent"] = 0.0,
            });
        }

        return new BrokerBook(rows, new JsonObject { ["totalholdingvalue"] = BookFormatting.RoundPy(value, 2), ["totalinvvalue"] = BookFormatting.RoundPy(investment, 2), ["totalprofitandloss"] = 0.0, ["totalpnlpercentage"] = 0.0 });
    }

    public async Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var (data, error) = await ReadAsync(session, "/funds", null, cancellationToken);
        if (error is not null || data is not JsonObject d || d.Count == 0) throw new BrokerException($"{Display} could not send the funds: {error ?? "no reason was given."}", 500);
        var withdrawal = Float(d["withdrawal_balance"]);
        var available = withdrawal;
        if (d["detailed_avl_balance"] is JsonObject detail)
            foreach (var key in new[] { "eq_cnc", "eq_mis", "eq_mtf" })
                if (detail[key] is { } v)
                {
                    available = Float(v);
                    break;
                }

        string F(double x) => BookFormatting.Fixed2(x);
        return new JsonObject
        {
            ["availablecash"] = F(available), ["collateral"] = F(Float(d["pledge_received"])), ["m2mrealized"] = F(Float(d["realized_pnl"])), ["m2munrealized"] = F(Float(d["unrealized_pnl"])), ["utiliseddebits"] = F(Math.Max(0, Float(d["sod_balance"]) - available)),
        };
    }
}
