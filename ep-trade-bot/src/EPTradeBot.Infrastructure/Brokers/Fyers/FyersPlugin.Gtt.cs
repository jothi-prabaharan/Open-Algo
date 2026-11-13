using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Fyers;

/// <summary>
/// Fyers GTT (broker/fyers/api/gtt_api.py and mapping/gtt_data.py). A trigger leg has only a price, a trigger and a quantity, so the order it fires is always
/// a LIMIT and a MARKET request becomes a protected LIMIT. An OCO is two legs of one trigger, the one above the last price first. The strategy name
/// travels as the order tag, reduced to letters and digits.
/// </summary>
public sealed partial class FyersPlugin : IBrokerGtt
{
    private const string GttSync = "/api/v3/gtt/orders/sync";
    private const string GttBook = "/api/v3/gtt/orders";

    public async Task<GttResult> PlaceGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var (price, stopLoss, target) = await ProtectedPricesAsync(session, order, cancellationToken);
        var body = new JsonObject
        {
            ["side"] = string.Equals(order.Action, "BUY", StringComparison.OrdinalIgnoreCase) ? 1 : -1,
            ["symbol"] = order.Instrument.BrokerSymbol,
            ["productType"] = Product(order.Product),
            ["orderInfo"] = OrderInfo(order, price, stopLoss, target),
            ["orderTag"] = OrderTag(order.Strategy),
        };

        var (status, reply) = await SendGttAsync(HttpMethod.Post, GttSync, session, body, cancellationToken);
        if (GttOk(reply) && Str(reply?["id"]) is { Length: > 0 } id) return GttResult.Success(id);
        // The service turns a refusal that carries an OK status into a failure status.
        return GttResult.Refused(status, Str(reply?["message"]) ?? "Failed to place GTT");
    }

    public async Task<GttResult> ModifyGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var triggerId = order.TriggerId ?? "";
        if (triggerId.Length == 0) return GttResult.Refused(400, "trigger_id is required");

        // Side, symbol and product cannot change on an existing trigger, so only the legs go.
        var (price, stopLoss, target) = await ProtectedPricesAsync(session, order, cancellationToken);
        var body = new JsonObject { ["id"] = triggerId, ["orderInfo"] = OrderInfo(order, price, stopLoss, target) };
        var (status, reply) = await SendGttAsync(HttpMethod.Patch, GttSync, session, body, cancellationToken);
        return GttWriteResult(status, reply, triggerId, "Failed to modify GTT");
    }

    public async Task<GttResult> CancelGttAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(triggerId)) return GttResult.Refused(400, "trigger_id is required");
        var (status, reply) = await SendGttAsync(HttpMethod.Delete, GttSync, session, new JsonObject { ["id"] = triggerId }, cancellationToken);
        return GttWriteResult(status, reply, triggerId, "Failed to cancel GTT");
    }

    public async Task<JsonArray> GetGttBookAsync(BrokerSession session, bool includeHistory, CancellationToken cancellationToken)
    {
        var (status, reply) = await SendGttAsync(HttpMethod.Get, GttBook, session, null, cancellationToken);
        if (!GttOk(reply)) throw new BrokerException(Str(reply?["message"]) ?? "Failed to fetch GTT book", status == 200 ? 500 : status);

        var book = new JsonArray();
        foreach (var row in (reply!["orderBook"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // Only a trigger still waiting (in transit or pending) can fire.
            var rawStatus = (int)Float(row["ord_status"]);
            if (rawStatus is not (4 or 6)) continue;

            var exchange = ((int)Float(row["exchange"]), (int)Float(row["segment"])) switch
            {
                (10, 10) => "NSE", (10, 11) => "NFO", (10, 12) => "CDS", (12, 10) => "BSE", (12, 11) => "BFO", (11, 20) => "MCX", _ => "Unknown Exchange",
            };
            var brokerSymbol = Str(row["symbol"]) ?? "";
            var record = brokerSymbol.Length > 0 ? await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken) : null;
            var action = (int)Float(row["tran_side"]) == 1 ? "BUY" : "SELL";
            var rawProduct = Str(row["product_type"]) ?? "";
            var product = rawProduct switch { "CNC" => "CNC", "MARGIN" => "NRML", "INTRADAY" => "MIS", "CO" => "CO", "BO" => "BO", _ => rawProduct };

            var ind = (int)Float(row["gtt_oco_ind"]);
            var legs = new JsonArray { BookLeg(action, product, row["price_limit"], row["qty"]) };
            var triggers = new List<double> { Float(row["price_trigger"]) };
            if (ind == 2)
            {
                legs.Add(BookLeg(action, product, row["price2_limit"], row["qty2"]));
                triggers.Add(Float(row["price2_trigger"]));
                // OpenAlgo shows the triggers low to high; Fyers lists the one above the last price first.
                triggers.Sort();
            }

            book.Add(new JsonObject
            {
                ["trigger_id"] = Str(row["id"]) ?? "",
                ["trigger_type"] = ind switch { 1 => "single", 2 => "two-leg", _ => "SINGLE" },
                ["status"] = rawStatus == 4 ? "transit" : "active",
                ["symbol"] = record?.Symbol is { Length: > 0 } s ? s : brokerSymbol,
                ["exchange"] = exchange,
                ["trigger_prices"] = new JsonArray(triggers.Select(t => (JsonNode?)t).ToArray()),
                ["last_price"] = Float(row["ltp"]),
                ["legs"] = legs,
                ["created_at"] = Str(row["create_time"]) ?? "",
                ["updated_at"] = "",
                ["expires_at"] = "",
            });
        }

        return book;
    }

    // ---- request ---------------------------------------------------------------------------------------------

    private static JsonObject BookLeg(string action, string product, JsonNode? price, JsonNode? quantity) => new()
    {
        ["action"] = action, ["quantity"] = (int)Float(quantity), ["price"] = Float(price), ["pricetype"] = "LIMIT", ["product"] = product,
    };

    private static JsonObject Leg(double price, double trigger, int quantity) => new() { ["price"] = price, ["triggerPrice"] = trigger, ["qty"] = quantity };

    /// <summary>The single leg, or for an OCO the target leg (above the last price) and the stoploss leg (below it).</summary>
    private static JsonObject OrderInfo(GttOrder order, double price, double stopLoss, double target) =>
        order.IsOco
            ? new JsonObject { ["leg1"] = Leg(target, (double)order.TriggerTg, order.Quantity), ["leg2"] = Leg(stopLoss, (double)order.TriggerSl, order.Quantity) }
            : new JsonObject { ["leg1"] = Leg(price, (double)order.Trigger, order.Quantity) };

    /// <summary>A MARKET request: no leg has an order type, so each limit is the price moved the protection slab beyond the reference, rounded to the tick.</summary>
    private async Task<(double Price, double StopLoss, double Target)> ProtectedPricesAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        double price = (double)order.Price, stopLoss = (double)order.StopLoss, target = (double)order.Target;
        if (!string.Equals(order.PriceType, "MARKET", StringComparison.OrdinalIgnoreCase)) return (price, stopLoss, target);

        var type = MarketPriceProtection.InstrumentType(order.Instrument.InstrumentType, order.Instrument.Symbol);
        double Protect(double reference) => MarketPriceProtection.ProtectedPrice(reference, order.Action, type, order.Instrument.TickSize);
        if (order.IsOco)
        {
            // Each leg is protected around its own trigger.
            if (order.TriggerSl > 0) stopLoss = Protect((double)order.TriggerSl);
            if (order.TriggerTg > 0) target = Protect((double)order.TriggerTg);
        }
        else
        {
            var last = await LastPriceAsync(session, order, cancellationToken);
            if (last > 0) price = Protect(last);
        }

        return (price, stopLoss, target);
    }

    private async Task<double> LastPriceAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        try
        {
            return Float((await GetQuoteAsync(session, order.Instrument.Symbol, order.Instrument.Exchange, cancellationToken))["ltp"]);
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Fyers GTT could not read the last price of {Symbol}.", order.Instrument.Symbol);
            return 0;
        }
    }

    /// <summary>The strategy name as Fyers accepts a tag: letters and digits only, at most 20, or "openalgo".</summary>
    private static string OrderTag(string? strategy)
    {
        var cleaned = new string((strategy ?? "").Trim().Where(char.IsLetterOrDigit).ToArray());
        if (cleaned.Length > 20) cleaned = cleaned[..20];
        return cleaned.Length > 0 ? cleaned : "openalgo";
    }

    /// <summary>Fyers says "ok" or "error"; an answer without that word is judged by its code.</summary>
    private static bool GttOk(JsonObject? reply)
    {
        if (reply is null) return false;
        var word = (Str(reply["s"]) ?? "").ToLowerInvariant();
        if (word == "ok") return true;
        if (word == "error") return false;
        return reply["code"] is not null && (int)Float(reply["code"]) is 200 or 201 or 1101 or 1102 or 1103;
    }

    private async Task<(int Status, JsonObject? Reply)> SendGttAsync(HttpMethod method, string path, BrokerSession session, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = Request(session, method, path, body);
        using var response = await SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, await ReadAsync(response, cancellationToken) as JsonObject);
    }

    private static GttResult GttWriteResult(int status, JsonObject? reply, string triggerId, string failure)
    {
        if (GttOk(reply)) return GttResult.Success(Str(reply!["id"]) is { Length: > 0 } id ? id : triggerId);
        // OpenAlgo reports a rejection that arrives inside an OK answer as a success; here the trigger was not changed, so it is a failure.
        return GttResult.Refused(status == 200 ? 500 : status, Str(reply?["message"]) ?? failure);
    }
}
