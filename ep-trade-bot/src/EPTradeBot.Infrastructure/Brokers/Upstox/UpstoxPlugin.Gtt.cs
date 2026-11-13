using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Upstox;

/// <summary>
/// Upstox GTT (broker/upstox/api/gtt_api.py and mapping/gtt_data.py), on the v3 endpoints. Upstox has no limit price on a trigger, so the order fires at
/// the trigger price and a MARKET request becomes a market protection percentage. Every Upstox GTT must carry an ENTRY rule, so an OCO is sent as a
/// bracket that opens the position first; that is OpenAlgo's behaviour and it is kept, but it is the reason a stop or target on a holding already owned
/// should be a SINGLE sell trigger.
/// </summary>
public sealed partial class UpstoxPlugin : IBrokerGtt
{
    private const string GttPath = "/v3/order/gtt";
    private const string LastPriceFailed = "Failed to fetch last_price from Upstox quotes";

    private static readonly HashSet<string> LiveRuleStatuses = ["SCHEDULED", "PENDING", "OPEN", "INACTIVE"];

    public async Task<GttResult> PlaceGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var lastPrice = await LastPriceAsync(session, order, cancellationToken);
        if (order.IsOco && lastPrice <= 0) return GttResult.Refused(502, LastPriceFailed);

        var body = GttBody(order, lastPrice, forModify: false);
        using var request = Request(session, HttpMethod.Post, Api, GttPath + "/place", body);
        var (status, payload) = await SendGttAsync(request, cancellationToken);
        if (payload is null) return GttResult.Refused(500, "Failed to place GTT due to internal error");
        if (S(payload["status"]) == "success")
        {
            return GttId(payload) is { } id ? GttResult.Success(id) : GttResult.Refused(500, "Failed to place GTT");
        }

        // An error inside an OK answer is a failed placement, not a success.
        return GttResult.Refused(status == 200 ? 500 : status, GttErrorMessage(payload, "Failed to place GTT"));
    }

    public async Task<GttResult> ModifyGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var triggerId = order.TriggerId ?? "";
        if (triggerId.Length == 0) return GttResult.Refused(400, "trigger_id is required");
        var lastPrice = await LastPriceAsync(session, order, cancellationToken);
        if (order.IsOco && lastPrice <= 0) return GttResult.Refused(502, LastPriceFailed);

        var body = GttBody(order, lastPrice, forModify: true);
        using var request = Request(session, HttpMethod.Put, Api, GttPath + "/modify", body);
        return await GttWriteAsync(request, triggerId, "Failed to modify GTT", cancellationToken);
    }

    public async Task<GttResult> CancelGttAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(triggerId)) return GttResult.Refused(400, "trigger_id is required");
        using var request = Request(session, HttpMethod.Delete, Api, GttPath + "/cancel", new JsonObject { ["gtt_order_id"] = triggerId });
        return await GttWriteAsync(request, triggerId, "Failed to cancel GTT", cancellationToken);
    }

    public async Task<JsonArray> GetGttBookAsync(BrokerSession session, bool includeHistory, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Get, Api, GttPath, contentType: false);
        var (status, payload) = await SendGttAsync(request, cancellationToken);
        if (payload is null || S(payload["status"]) != "success")
            throw new BrokerException(GttErrorMessage(payload, "Failed to fetch GTT book"), status == 200 ? 500 : status);

        var book = new JsonArray();
        foreach (var gtt in (payload["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var rules = (gtt["rules"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (!rules.Any(r => LiveRuleStatuses.Contains(S(r["status"]).ToUpperInvariant()))) continue;

            var type = S(gtt["type"]) is { Length: > 0 } t ? t.ToUpperInvariant() : "SINGLE";
            var brokerExchange = S(gtt["exchange"]);
            var exchange = BookExchange(brokerExchange);
            var brokerSymbol = S(gtt["trading_symbol"]);
            var record = brokerSymbol.Length > 0 && exchange.Length > 0 ? await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken) : null;
            var quantity = gtt["quantity"]?.DeepClone() ?? 0;
            var product = ReverseProduct(exchange, S(gtt["product"]));

            // The mandatory entry rule of a bracket has no slot in OpenAlgo's two-leg shape, so it is left out and the pair reads low to high.
            var shown = type == "MULTIPLE" ? rules.Where(r => S(r["strategy"]).ToUpperInvariant() != "ENTRY").ToList() : rules;
            if (shown.Count == 0) shown = rules;
            shown = [.. shown.OrderBy(r => Float(r["trigger_price"]))];

            var legs = new JsonArray();
            foreach (var rule in shown)
            {
                legs.Add(new JsonObject
                {
                    ["action"] = S(rule["transaction_type"]).ToUpperInvariant(), ["quantity"] = quantity.DeepClone(), ["price"] = Float(rule["trigger_price"]),
                    ["pricetype"] = "LIMIT", ["product"] = product,
                });
            }

            book.Add(new JsonObject
            {
                ["trigger_id"] = S(gtt["gtt_order_id"]),
                ["trigger_type"] = type == "MULTIPLE" ? "two-leg" : "single",
                ["status"] = "active",
                ["symbol"] = record?.Symbol is { Length: > 0 } s ? s : brokerSymbol,
                ["exchange"] = exchange,
                ["trigger_prices"] = new JsonArray(shown.Select(r => (JsonNode?)Float(r["trigger_price"])).ToArray()),
                ["last_price"] = 0,
                ["legs"] = legs,
                ["created_at"] = IsoTimestamp(gtt["created_at"]),
                ["updated_at"] = "",
                ["expires_at"] = IsoTimestamp(gtt["expires_at"]),
            });
        }

        return book;
    }

    // ---- request ---------------------------------------------------------------------------------------------

    /// <summary>The last price that decides an entry's direction, or 0 when Upstox would not give one.</summary>
    private async Task<double> LastPriceAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        try
        {
            var quote = await QuoteAsync(session, order.Instrument.Symbol, order.Instrument.Exchange, cancellationToken);
            return Float(quote["last_price"]);
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Upstox GTT could not read the last price of {Symbol}.", order.Instrument.Symbol);
            return 0;
        }
    }

    private static string S(JsonNode? node) => Str(node) ?? "";

    private static JsonObject GttRule(string strategy, string triggerType, double triggerPrice, int? marketProtection)
    {
        var rule = new JsonObject { ["strategy"] = strategy, ["trigger_type"] = triggerType, ["trigger_price"] = triggerPrice };
        if (marketProtection is { } protection) rule["market_protection"] = protection;
        return rule;
    }

    /// <summary>Upstox refuses MARKET child orders, so a MARKET request becomes a protection band sized from the same slabs the other brokers use (the slabs top out at 5, inside Upstox's 1 to 25).</summary>
    private static int? MarketProtection(GttOrder order, double basePrice)
    {
        if (!string.Equals(order.PriceType, "MARKET", StringComparison.OrdinalIgnoreCase)) return null;
        var type = MarketPriceProtection.InstrumentType(order.Instrument.InstrumentType, order.Instrument.Symbol);
        var percent = MarketPriceProtection.Percentage(basePrice, type);
        return Math.Max(1, (int)Math.Round(percent, MidpointRounding.ToEven));
    }

    private static JsonObject GttBody(GttOrder order, double lastPrice, bool forModify)
    {
        JsonArray rules;
        string type;
        string transaction;
        if (order.IsOco)
        {
            // The exit side is the order's action; Upstox wants the entry side, which is the opposite, and the profit side follows it.
            var entry = string.Equals(order.Action, "BUY", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
            var (target, stop) = entry == "BUY" ? ((double)order.TriggerTg, (double)order.TriggerSl) : ((double)order.TriggerSl, (double)order.TriggerTg);
            rules =
            [
                GttRule("ENTRY", "IMMEDIATE", lastPrice, MarketProtection(order, lastPrice)),
                GttRule("TARGET", "IMMEDIATE", target, MarketProtection(order, target)),
                GttRule("STOPLOSS", "IMMEDIATE", stop, MarketProtection(order, stop)),
            ];
            type = "MULTIPLE";
            transaction = entry;
        }
        else
        {
            var trigger = (double)order.Trigger;
            // Upstox checks the direction against the live price; without one the field the trader filled in decides.
            var direction = lastPrice > 0
                ? trigger < lastPrice ? "BELOW" : trigger > lastPrice ? "ABOVE" : "IMMEDIATE"
                : order.TriggerTg > 0 ? "ABOVE" : "BELOW";
            rules = [GttRule("ENTRY", direction, trigger, MarketProtection(order, trigger))];
            type = "SINGLE";
            transaction = order.Action.ToUpperInvariant();
        }

        if (!forModify)
        {
            return new JsonObject
            {
                ["type"] = type, ["quantity"] = order.Quantity, ["product"] = Product(order.Product), ["instrument_token"] = order.Instrument.Token,
                ["transaction_type"] = transaction, ["rules"] = rules,
            };
        }

        // The modify body is narrower: the instrument, product and side cannot change, and the protection set at placement stays.
        foreach (var rule in rules.OfType<JsonObject>()) rule.Remove("market_protection");
        return new JsonObject { ["type"] = type, ["quantity"] = order.Quantity, ["gtt_order_id"] = order.TriggerId, ["rules"] = rules };
    }

    private async Task<(int Status, JsonObject? Payload)> SendGttAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, await ReadAsync(response, cancellationToken) as JsonObject);
    }

    private async Task<GttResult> GttWriteAsync(HttpRequestMessage request, string triggerId, string failure, CancellationToken cancellationToken)
    {
        var (status, payload) = await SendGttAsync(request, cancellationToken);
        if (payload is not null && S(payload["status"]) == "success") return GttResult.Success(GttId(payload) ?? triggerId);
        // OpenAlgo reports a rejection that arrives inside an OK answer as a success; here it is a failure, because the trigger was not changed.
        return GttResult.Refused(status == 200 ? 500 : status, GttErrorMessage(payload, failure));
    }

    /// <summary>The id Upstox returns: the first of <c>gtt_order_ids</c>, else <c>gtt_order_id</c>.</summary>
    private static string? GttId(JsonObject payload)
    {
        if (payload["data"] is JsonObject data)
        {
            if (data["gtt_order_ids"] is JsonArray { Count: > 0 } ids && S(ids[0]) is { Length: > 0 } first) return first;
            if (S(data["gtt_order_id"]) is { Length: > 0 } single) return single;
        }

        return null;
    }

    /// <summary>The reasons in Upstox's <c>errors</c> list ("CODE: message", joined), else its flat message, else the given default.</summary>
    private static string GttErrorMessage(JsonObject? payload, string fallback)
    {
        if (payload is null) return fallback;
        if (payload["errors"] is JsonArray { Count: > 0 } errors)
        {
            var parts = new List<string>();
            foreach (var error in errors.OfType<JsonObject>())
            {
                var code = S(error["error_code"]);
                var message = S(error["message"]);
                parts.Add(code.Length > 0 ? $"{code}: {message}".Trim(':', ' ') : message);
            }

            parts = [.. parts.Where(p => p.Length > 0)];
            if (parts.Count > 0) return string.Join("; ", parts);
        }

        return S(payload["message"]) is { Length: > 0 } flat ? flat : fallback;
    }

    // ---- book ------------------------------------------------------------------------------------------------

    private static string BookExchange(string brokerExchange) => brokerExchange switch
    {
        "NSE_EQ" => "NSE", "NSE_FO" => "NFO", "NSE_CD" => "CDS", "BSE_EQ" => "BSE", "BSE_FO" => "BFO", "MCX_FO" => "MCX",
        "NSE_INDEX" => "NSE_INDEX", "BSE_INDEX" => "BSE_INDEX", _ => "NSE",
    };

    private static string ReverseProduct(string exchange, string product) => product switch
    {
        "D" => exchange switch { "NSE" or "BSE" => "CNC", "NFO" or "BFO" or "MCX" or "CDS" => "NRML", _ => "" },
        "I" => "MIS",
        _ => product,
    };

    /// <summary>Upstox stamps a GTT with an epoch whose unit it does not name (microseconds in practice), so the unit is read from the size. Anything unreadable is blank.</summary>
    private static string IsoTimestamp(JsonNode? value)
    {
        var text = S(value).Trim();
        if (text.Length == 0) return "";
        if (!text.TrimStart('-').All(char.IsAsciiDigit)) return text;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch) || epoch <= 0) return "";

        double seconds;
        if (epoch >= 1e18) seconds = epoch / 1e9;
        else if (epoch >= 1e15) seconds = epoch / 1e6;
        else if (epoch >= 1e12) seconds = epoch / 1e3;
        else if (epoch >= 1e9) seconds = epoch;
        else return "";

        return DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
