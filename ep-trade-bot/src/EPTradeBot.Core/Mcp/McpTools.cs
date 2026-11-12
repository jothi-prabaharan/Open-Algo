using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;
using EPTradeBot.Core.Indicators;

namespace EPTradeBot.Core.Mcp;

/// <summary>The tools MCP clients see. Ported from OpenAlgo's MCP server (names, parameters and defaults), each running against the platform's own API for one account.</summary>
public static class McpTools
{
    public const string Orders = "orders";
    public const string Account = "account";
    public const string MarketData = "marketdata";
    public const string Research = "research";
    public const string System = "system";

    /// <summary>The label every order from MCP carries, so MCP activity can be told apart in the logs.</summary>
    public const string DefaultStrategy = "python mcp";

    private static readonly string[] Exchanges = ["NSE", "NFO", "CDS", "BSE", "BFO", "BCD", "MCX", "NCDEX", "NCO", "NSE_INDEX", "BSE_INDEX", "MCX_INDEX", "GLOBAL_INDEX", "CRYPTO"];

    // ---- schema helpers ----------------------------------------------------------------------------------

    private static JsonObject Prop(string type, string description, string[]? values = null, string? items = null)
    {
        var o = new JsonObject { ["type"] = type, ["description"] = description };
        if (values is not null) o["enum"] = new JsonArray([.. values.Select(v => (JsonNode?)v)]);
        if (items is not null) o["items"] = new JsonObject { ["type"] = items };
        return o;
    }

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Prop)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, o) in props) p[n] = o.DeepClone();
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = new JsonArray([.. required.Select(r => (JsonNode?)r)]), ["additionalProperties"] = false };
    }

    private static readonly (string, JsonObject) Symbol = ("symbol", Prop("string", "The symbol, e.g. SBIN or NIFTY28MAR2420800CE."));
    private static readonly (string, JsonObject) Exchange = ("exchange", Prop("string", "The exchange. Defaults to NSE.", Exchanges));

    private static readonly (string, JsonObject)[] OrderFields =
    [
        ("symbol", Prop("string", "The symbol, e.g. SBIN.")),
        ("quantity", Prop("integer", "Whole units, at least 1.")),
        ("action", Prop("string", "BUY or SELL.", ["BUY", "SELL"])),
        Exchange,
        ("price_type", Prop("string", "MARKET, LIMIT, SL or SL-M. Defaults to MARKET.", ["MARKET", "LIMIT", "SL", "SL-M"])),
        ("product", Prop("string", "MIS (intraday), NRML (carry) or CNC (delivery). Defaults to MIS.", ["MIS", "NRML", "CNC"])),
        ("strategy", Prop("string", "A label for the order. Defaults to \"python mcp\".")),
        ("price", Prop("number", "For LIMIT and SL orders.")),
        ("trigger_price", Prop("number", "For SL and SL-M orders.")),
        ("disclosed_quantity", Prop("integer", "Part of the quantity to disclose.")),
    ];

    // ---- argument helpers --------------------------------------------------------------------------------

    private static string Str(JsonObject a, string key, string fallback) => a[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : fallback;

    private static string? OptStr(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static JsonNode? Num(JsonObject a, string key, double fallback) => a[key] is JsonValue v && FlowData.TryParseOrNumber(v, out var d) ? d : fallback;

    private static JsonNode? Whole(JsonObject a, string key, long fallback) => a[key] is JsonValue v && FlowData.TryParseOrNumber(v, out var d) ? (long)d : fallback;

    private static JsonObject Order(JsonObject a, bool smart = false)
    {
        var o = new JsonObject
        {
            ["strategy"] = Str(a, "strategy", DefaultStrategy), ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "NSE").ToUpperInvariant(),
            ["action"] = Str(a, "action", "").ToUpperInvariant(), ["quantity"] = Whole(a, "quantity", 0), ["pricetype"] = Str(a, "price_type", "MARKET").ToUpperInvariant(),
            ["product"] = Str(a, "product", "MIS").ToUpperInvariant(), ["price"] = Num(a, "price", 0), ["trigger_price"] = Num(a, "trigger_price", 0),
            ["disclosed_quantity"] = Whole(a, "disclosed_quantity", 0),
        };
        if (smart) o["position_size"] = Whole(a, "position_size", 0);
        return o;
    }

    private static Func<JsonObject, McpSession, CancellationToken, Task<JsonObject>> Call(string api, Func<JsonObject, JsonObject> body) =>
        (a, s, ct) => s.Api.CallAsync(api, body(a), ct);

    private static McpTool Read(string name, string toolset, string description, JsonObject schema, Func<JsonObject, McpSession, CancellationToken, Task<JsonObject>> run) =>
        new(name, description, toolset, true, false, schema, run);

    private static McpTool Write(string name, string description, JsonObject schema, Func<JsonObject, McpSession, CancellationToken, Task<JsonObject>> run, bool destructive = false) =>
        new(name, description, Orders, false, destructive, schema, run);

    // ---- the catalog -------------------------------------------------------------------------------------

    public static IReadOnlyList<McpTool> All { get; } =
    [
        // Orders. A write goes through the guard, the kill switches and the risk limits like any order, and waits in the Action Center when the grant is semi-auto.
        Write("place_order", "Place a single order.", Schema(["symbol", "quantity", "action"], OrderFields), Call("placeorder", a => Order(a))),
        Write("place_smart_order", "Place an order that moves the position to a target size, sending only the difference.",
            Schema(["symbol", "quantity", "action", "position_size"], [.. OrderFields, ("position_size", Prop("integer", "The net position wanted after the order, in units; negative is short."))]),
            Call("placesmartorder", a => Order(a, smart: true))),
        Write("place_basket_order", "Place several orders in one call.",
            Schema(["orders"], ("orders", Prop("array", "The orders: each with symbol, exchange, action, quantity, pricetype and product.", items: "object")), ("strategy", Prop("string", "A label for the orders."))),
            Call("basketorder", a => new JsonObject { ["strategy"] = Str(a, "strategy", DefaultStrategy), ["orders"] = a["orders"]?.DeepClone() })),
        Write("place_split_order", "Break a large order into equal chunks.",
            Schema(["symbol", "quantity", "action", "split_size"], [.. OrderFields, ("split_size", Prop("integer", "Units in each chunk."))]),
            Call("splitorder", a => { var o = Order(a); o["splitsize"] = Whole(a, "split_size", 0); return o; })),
        Write("place_options_order", "Place an options order by underlying, expiry and strike offset (ATM, ITM1, OTM2, ...).",
            Schema(["underlying", "exchange", "offset", "option_type", "action", "quantity"],
                ("underlying", Prop("string", "The underlying, e.g. NIFTY.")), ("exchange", Prop("string", "NSE_INDEX, NFO, BSE_INDEX, BFO or MCX.")), ("expiry_date", Prop("string", "Like 28MAR24.")),
                ("offset", Prop("string", "ATM, ITM1..ITM50 or OTM1..OTM50.")), ("option_type", Prop("string", "CE or PE.", ["CE", "PE"])), ("action", Prop("string", "BUY or SELL.", ["BUY", "SELL"])),
                ("quantity", Prop("integer", "Units (a multiple of the lot size).")), ("price_type", Prop("string", "MARKET, LIMIT, SL or SL-M.")), ("product", Prop("string", "MIS or NRML.", ["MIS", "NRML"])),
                ("strategy", Prop("string", "A label.")), ("price", Prop("number", "For LIMIT and SL.")), ("trigger_price", Prop("number", "For SL and SL-M."))),
            Call("optionsorder", a => new JsonObject
            {
                ["strategy"] = Str(a, "strategy", DefaultStrategy), ["underlying"] = Str(a, "underlying", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "").ToUpperInvariant(),
                ["expiry_date"] = OptStr(a, "expiry_date"), ["offset"] = Str(a, "offset", "ATM").ToUpperInvariant(), ["option_type"] = Str(a, "option_type", "").ToUpperInvariant(),
                ["action"] = Str(a, "action", "").ToUpperInvariant(), ["quantity"] = Whole(a, "quantity", 0), ["pricetype"] = Str(a, "price_type", "MARKET").ToUpperInvariant(),
                ["product"] = Str(a, "product", "MIS").ToUpperInvariant(), ["price"] = Num(a, "price", 0), ["trigger_price"] = Num(a, "trigger_price", 0),
            })),
        Write("place_options_multi_order", "Place a multi-leg options structure (a straddle, an iron condor, ...).",
            Schema(["underlying", "exchange", "legs"], ("underlying", Prop("string", "The underlying.")), ("exchange", Prop("string", "NSE_INDEX, NFO, BSE_INDEX, BFO or MCX.")), ("expiry_date", Prop("string", "Like 28MAR24.")),
                ("legs", Prop("array", "Each leg: offset, option_type, action, quantity, expiry_date, pricetype, product.", items: "object")), ("strategy", Prop("string", "A label."))),
            Call("optionsmultiorder", a => new JsonObject
            {
                ["strategy"] = Str(a, "strategy", DefaultStrategy), ["underlying"] = Str(a, "underlying", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "").ToUpperInvariant(),
                ["expiry_date"] = OptStr(a, "expiry_date"), ["legs"] = a["legs"]?.DeepClone(),
            })),
        Write("modify_order", "Change the price, trigger or quantity of an open order.",
            Schema(["order_id", "symbol", "action", "exchange", "price_type", "product", "quantity", "price"],
                ("order_id", Prop("string", "The order to change.")), ("symbol", Prop("string", "The symbol.")), ("action", Prop("string", "BUY or SELL.", ["BUY", "SELL"])), Exchange,
                ("price_type", Prop("string", "MARKET, LIMIT, SL or SL-M.")), ("product", Prop("string", "MIS, NRML or CNC.")), ("quantity", Prop("integer", "The new quantity.")),
                ("price", Prop("number", "The new price.")), ("trigger_price", Prop("number", "The new trigger price.")), ("disclosed_quantity", Prop("integer", "Disclosed quantity.")), ("strategy", Prop("string", "A label."))),
            Call("modifyorder", a => new JsonObject
            {
                ["strategy"] = Str(a, "strategy", DefaultStrategy), ["orderid"] = Str(a, "order_id", ""), ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "NSE").ToUpperInvariant(),
                ["action"] = Str(a, "action", "").ToUpperInvariant(), ["product"] = Str(a, "product", "MIS").ToUpperInvariant(), ["pricetype"] = Str(a, "price_type", "MARKET").ToUpperInvariant(),
                ["quantity"] = Whole(a, "quantity", 0), ["price"] = Num(a, "price", 0), ["trigger_price"] = Num(a, "trigger_price", 0), ["disclosed_quantity"] = Whole(a, "disclosed_quantity", 0),
            }), destructive: true),
        Write("cancel_order", "Cancel one open order.", Schema(["order_id"], ("order_id", Prop("string", "The order to cancel.")), ("strategy", Prop("string", "A label."))),
            Call("cancelorder", a => new JsonObject { ["strategy"] = Str(a, "strategy", DefaultStrategy), ["orderid"] = Str(a, "order_id", "") }), destructive: true),
        Write("cancel_all_orders", "Cancel every open order.", Schema([], ("strategy", Prop("string", "A label."))),
            Call("cancelallorder", a => new JsonObject { ["strategy"] = Str(a, "strategy", DefaultStrategy) }), destructive: true),
        Write("close_all_positions", "Square off every open position.", Schema([], ("strategy", Prop("string", "A label."))),
            Call("closeposition", a => new JsonObject { ["strategy"] = Str(a, "strategy", DefaultStrategy) }), destructive: true),

        // Account.
        Read("get_open_position", Account, "The open quantity of one instrument.", Schema(["symbol", "product"], Symbol, Exchange, ("product", Prop("string", "MIS, NRML or CNC.", ["MIS", "NRML", "CNC"])), ("strategy", Prop("string", "A label."))),
            Call("openposition", a => new JsonObject { ["strategy"] = Str(a, "strategy", DefaultStrategy), ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "NSE").ToUpperInvariant(), ["product"] = Str(a, "product", "MIS").ToUpperInvariant() })),
        Read("get_position_book", Account, "All open positions.", Schema([]), Call("positionbook", _ => [])),
        Read("get_holdings", Account, "Long-term holdings.", Schema([]), Call("holdings", _ => [])),
        Read("get_funds", Account, "Cash and margin.", Schema([]), Call("funds", _ => [])),
        Read("get_order_status", Account, "The status of one order.", Schema(["order_id"], ("order_id", Prop("string", "The order.")), ("strategy", Prop("string", "A label."))),
            Call("orderstatus", a => new JsonObject { ["strategy"] = Str(a, "strategy", DefaultStrategy), ["orderid"] = Str(a, "order_id", "") })),
        Read("get_order_book", Account, "Today's orders.", Schema([]), Call("orderbook", _ => [])),
        Read("get_trade_book", Account, "Today's executed trades.", Schema([]), Call("tradebook", _ => [])),

        // Market data.
        Read("get_quote", MarketData, "The latest price and day range of one instrument.", Schema(["symbol"], Symbol, Exchange),
            Call("quotes", a => new JsonObject { ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "NSE").ToUpperInvariant() })),
        Read("get_multi_quotes", MarketData, "Quotes for several instruments at once.", Schema(["symbols"], ("symbols", Prop("array", "Each: {\"symbol\": ..., \"exchange\": ...}.", items: "object"))),
            Call("multiquotes", a => new JsonObject { ["symbols"] = a["symbols"]?.DeepClone() })),
        Read("get_market_depth", MarketData, "The best bids and offers.", Schema(["symbol"], Symbol, Exchange),
            Call("depth", a => new JsonObject { ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "NSE").ToUpperInvariant() })),
        Read("get_historical_data", MarketData, "Candles for a date range.", Schema(["symbol", "interval", "start_date", "end_date"], Symbol, Exchange,
                ("interval", Prop("string", "1m, 5m, 15m, 1h, D, ... (see get_available_intervals).")), ("start_date", Prop("string", "YYYY-MM-DD.")), ("end_date", Prop("string", "YYYY-MM-DD.")),
                ("source", Prop("string", "api (the broker) or db (stored).", ["api", "db"]))),
            Call("history", a => new JsonObject
            {
                ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "NSE").ToUpperInvariant(), ["interval"] = Str(a, "interval", ""),
                ["start_date"] = Str(a, "start_date", ""), ["end_date"] = Str(a, "end_date", ""), ["source"] = Str(a, "source", "api"),
            })),
        Read("get_option_chain", MarketData, "The option chain of an underlying for one expiry.",
            Schema(["underlying", "exchange", "expiry_date"], ("underlying", Prop("string", "The underlying.")), ("exchange", Prop("string", "NSE_INDEX, BSE_INDEX, NFO, BFO or MCX.")), ("expiry_date", Prop("string", "Like 28MAR24.")),
                ("strike_count", Prop("integer", "Strikes each side of the money.")), ("with_greeks", Prop("boolean", "Include greeks."))),
            Call("optionchain", a => new JsonObject
            {
                ["underlying"] = Str(a, "underlying", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "").ToUpperInvariant(), ["expiry_date"] = Str(a, "expiry_date", "").ToUpperInvariant(),
                ["strike_count"] = a["strike_count"]?.DeepClone(), ["with_greeks"] = a["with_greeks"]?.DeepClone() ?? false,
            })),
        Read("search_instruments", MarketData, "Find instruments by name.", Schema(["query"], ("query", Prop("string", "Part of a name.")), ("exchange", Prop("string", "Limit to one exchange."))),
            Call("search", a => new JsonObject { ["query"] = Str(a, "query", ""), ["exchange"] = OptStr(a, "exchange")?.ToUpperInvariant() })),
        Read("get_symbol_info", MarketData, "Lot size, tick size, expiry and token of one instrument.", Schema(["symbol"], Symbol, Exchange),
            Call("symbol", a => new JsonObject { ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "NSE").ToUpperInvariant() })),
        Read("get_option_symbol", MarketData, "The option contract for an underlying, expiry, strike offset and type.",
            Schema(["underlying", "exchange", "offset", "option_type"], ("underlying", Prop("string", "The underlying.")), ("exchange", Prop("string", "The exchange.")), ("expiry_date", Prop("string", "Like 28MAR24.")),
                ("offset", Prop("string", "ATM, ITM1.., OTM1..")), ("option_type", Prop("string", "CE or PE.", ["CE", "PE"]))),
            Call("optionsymbol", a => new JsonObject
            {
                ["strategy"] = DefaultStrategy, ["underlying"] = Str(a, "underlying", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "").ToUpperInvariant(), ["expiry_date"] = OptStr(a, "expiry_date"),
                ["offset"] = Str(a, "offset", "ATM").ToUpperInvariant(), ["option_type"] = Str(a, "option_type", "").ToUpperInvariant(),
            })),
        Read("get_synthetic_future", MarketData, "The synthetic future price from the at-the-money call and put.",
            Schema(["underlying", "exchange", "expiry_date"], ("underlying", Prop("string", "The underlying.")), ("exchange", Prop("string", "The exchange.")), ("expiry_date", Prop("string", "Like 28MAR24."))),
            Call("syntheticfuture", a => new JsonObject { ["underlying"] = Str(a, "underlying", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "").ToUpperInvariant(), ["expiry_date"] = Str(a, "expiry_date", "").ToUpperInvariant() })),
        Read("get_expiry_dates", MarketData, "The expiry dates of an underlying's futures or options.",
            Schema(["symbol", "exchange", "instrumenttype"], Symbol, ("exchange", Prop("string", "NFO, BFO, MCX or CDS.")), ("instrumenttype", Prop("string", "futures or options.", ["futures", "options"]))),
            Call("expiry", a => new JsonObject { ["symbol"] = Str(a, "symbol", "").ToUpperInvariant(), ["exchange"] = Str(a, "exchange", "").ToUpperInvariant(), ["instrumenttype"] = Str(a, "instrumenttype", "").ToLowerInvariant() })),
        Read("get_available_intervals", MarketData, "The candle intervals the broker offers.", Schema([]), Call("intervals", _ => [])),

        // Research.
        Read("calculate_indicator", Research, "One technical indicator over an instrument's recent candles.",
            Schema(["indicator", "symbol"], ("indicator", Prop("string", "A name such as rsi, sma, ema, macd, bbands, atr, supertrend.")), Symbol, Exchange,
                ("interval", Prop("string", "1m, 5m, 15m, 1h or D. Defaults to D.")), ("settings", Prop("object", "The indicator's settings, e.g. {\"period\": 14}.")),
                ("lookback_bars", Prop("integer", "Candles to look back, up to 200."))),
            Indicator),
        Read("list_indicators", Research, "The indicators calculate_indicator knows.", Schema([]),
            (_, _, _) => Task.FromResult(new JsonObject { ["status"] = "success", ["indicators"] = new JsonArray([.. IndicatorRegistry.Names.Where(n => IndicatorInputs.All.ContainsKey(n)).Select(n => (JsonNode?)n)]) })),

        // System.
        Read("analyzer_status", System, "Whether the account is in sandbox (analyzer) mode.", Schema([]), Call("analyzer", _ => [])),
        Read("get_holidays", System, "Market holidays for a year.", Schema([], ("year", Prop("integer", "The year; defaults to this one."))),
            Call("holidays", a => new JsonObject { ["year"] = a["year"]?.DeepClone() })),
        Read("get_timings", System, "The exchanges' trading hours on a date.", Schema(["date"], ("date", Prop("string", "YYYY-MM-DD."))),
            Call("timings", a => new JsonObject { ["date"] = Str(a, "date", "") })),
        Read("calculate_margin", System, "The margin an order or set of positions needs.",
            Schema(["positions"], ("positions", Prop("array", "Each: symbol, exchange, action, quantity, product, pricetype, price.", items: "object"))),
            Call("margin", a => new JsonObject { ["positions"] = a["positions"]?.DeepClone() })),
        Read("validate_order_constants", System, "The words an order is written in.", Schema([]),
            (_, _, _) => Task.FromResult(new JsonObject
            {
                ["status"] = "success",
                ["actions"] = new JsonArray([.. OrderVocabulary.Actions.Select(x => (JsonNode?)x)]), ["price_types"] = new JsonArray([.. OrderVocabulary.PriceTypes.Select(x => (JsonNode?)x)]),
                ["products"] = new JsonArray([.. OrderVocabulary.Products.Select(x => (JsonNode?)x)]), ["exchanges"] = new JsonArray([.. OrderVocabulary.Exchanges.Select(x => (JsonNode?)x)]),
            })),
    ];

    private static async Task<JsonObject> Indicator(JsonObject a, McpSession session, CancellationToken ct)
    {
        var symbol = Str(a, "symbol", "").ToUpperInvariant();
        var exchange = Str(a, "exchange", "NSE").ToUpperInvariant();
        var interval = Str(a, "interval", "D");
        var bars = IndicatorNode.ClampBars(a["lookback_bars"] is JsonValue v && FlowData.TryParseOrNumber(v, out var d) ? (long)d : 100);
        var end = DateOnly.FromDateTime(Time.Ist.FromUtc(session.UtcNow()));
        var start = end.AddDays(-IndicatorNode.CalendarDays(bars, interval));
        var history = await session.Api.CallAsync("history", new JsonObject
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["interval"] = interval, ["source"] = "api",
            ["start_date"] = start.ToString("yyyy-MM-dd"), ["end_date"] = end.ToString("yyyy-MM-dd"),
        }, ct);
        if (!FlowApiResults.IsSuccess(history)) return history;
        var records = (history["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (records.Count > IndicatorNode.MaxBars) records = records[^IndicatorNode.MaxBars..];
        try
        {
            return IndicatorNode.Compute(records, Str(a, "indicator", ""), a["settings"] as JsonObject ?? [], bars, 5, null, 0);
        }
        catch (ArgumentException ex)
        {
            return new JsonObject { ["status"] = "error", ["message"] = ex.Message };
        }
    }
}
