using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Angel;

/// <summary>
/// Angel One GTT rules (broker/angel/api/gtt_api.py and mapping/gtt_data.py). A SmartAPI rule has one trigger and one child order and
/// no OCO type, so an OCO is two rules, stoploss first, whose ids are handed back as one trigger id joined by "-". Angel keeps no
/// link between the two, and the book shows them as two rows.
/// </summary>
public sealed partial class AngelPlugin
{
    private const string GttBase = "/rest/secure/angelbroking/gtt/v1";

    /// <summary>A rule's validity in days. Angel validates the field, so an explicit value is sent.</summary>
    private const int GttDays = 365;

    private static readonly string[] ActiveGttStatuses = ["NEW", "ACTIVE", "SENTTOEXCHANGE"];

    private const int RuleListPageSize = 10, RuleListMaxPages = 25;

    private static readonly Dictionary<string, int> GttErrorStatus = new(StringComparer.Ordinal)
    {
        ["AB9000"] = 500, ["AB9002"] = 405, ["AB9003"] = 401, ["AB9005"] = 401, ["AB9013"] = 404,
    };

    public async Task<GttResult> PlaceGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var prepared = await ProtectedMarketAsync(session, order, cancellationToken);
        if (prepared.Refusal is not null) return prepared.Refusal;
        var legs = GttLegs(prepared.Order!);

        var created = new List<string>();
        foreach (var (label, trigger, limit) in legs)
        {
            var body = new JsonObject
            {
                ["tradingsymbol"] = order.Instrument.BrokerSymbol, ["symboltoken"] = order.Instrument.Token, ["exchange"] = order.Instrument.Exchange,
                ["transactiontype"] = order.Action, ["producttype"] = GttProduct(order.Product), ["price"] = Fmt(limit), ["qty"] = Fmt((decimal)order.Quantity),
                ["triggerprice"] = Fmt(trigger), ["disclosedqty"] = "0", ["timeperiod"] = GttDays,
            };
            var reply = await GttPostAsync(session, "createRule", body, cancellationToken);
            if (!GttOk(reply))
            {
                await RollBackAsync(session, created, order, cancellationToken);
                return GttResult.Refused(GttStatus(reply), GttMessage(reply, $"Failed to create GTT rule ({label})"));
            }

            if (RuleId(reply) is not { } id)
            {
                await RollBackAsync(session, created, order, cancellationToken);
                return GttResult.Refused(502, $"Angel returned no rule id for the {label} leg");
            }

            created.Add(id);
        }

        return GttResult.Success(string.Join('-', created));
    }

    public async Task<GttResult> ModifyGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        var triggerId = order.TriggerId ?? "";
        var ruleIds = SplitRuleIds(triggerId);
        if (ruleIds.Count == 0) return GttResult.Refused(400, "trigger_id is required");
        var expected = order.IsOco ? 2 : 1;
        if (ruleIds.Count != expected)
            return GttResult.Refused(400, $"trigger_id '{triggerId}' maps to {ruleIds.Count} Angel rule(s) but trigger_type={order.TriggerType} needs {expected}. Angel cannot convert a rule between SINGLE and OCO — cancel and re-place instead.");

        var prepared = await ProtectedMarketAsync(session, order, cancellationToken);
        if (prepared.Refusal is not null) return prepared.Refusal;

        var modified = new List<string>();
        var legs = GttLegs(prepared.Order!);
        for (var i = 0; i < legs.Count; i++)
        {
            var (label, trigger, limit) = legs[i];
            // Angel's modify body carries neither the side, the product nor the symbol, so those cannot change once a rule exists.
            var body = new JsonObject
            {
                ["id"] = ruleIds[i], ["symboltoken"] = order.Instrument.Token, ["exchange"] = order.Instrument.Exchange, ["price"] = Fmt(limit),
                ["qty"] = Fmt((decimal)order.Quantity), ["triggerprice"] = Fmt(trigger), ["disclosedqty"] = "0", ["timeperiod"] = GttDays,
            };
            var reply = await GttPostAsync(session, "modifyRule", body, cancellationToken);
            if (!GttOk(reply)) return GttResult.Refused(GttStatus(reply), $"{label} leg: {GttMessage(reply, $"Failed to modify GTT rule ({label})")}");
            modified.Add(RuleId(reply) ?? ruleIds[i]);
        }

        return GttResult.Success(string.Join('-', modified));
    }

    public async Task<GttResult> CancelGttAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken)
    {
        var ruleIds = SplitRuleIds(triggerId);
        if (ruleIds.Count == 0) return GttResult.Refused(400, "trigger_id is required");

        var cancelled = new List<string>();
        string Done() => cancelled.Count > 0 ? $" (already cancelled: {string.Join(", ", cancelled)})" : "";
        foreach (var ruleId in ruleIds)
        {
            // cancelRule needs the symbol token and exchange, which the cancel request does not carry, so the rule is read back first.
            var details = await GttPostAsync(session, "ruleDetails", new JsonObject { ["id"] = ruleId }, cancellationToken);
            if (!GttOk(details) || details!["data"] is not JsonObject rule) return GttResult.Refused(404, $"Could not fetch Angel GTT rule {ruleId}{Done()}");

            var body = new JsonObject { ["id"] = ruleId, ["symboltoken"] = Str(rule["symboltoken"]) ?? "", ["exchange"] = Str(rule["exchange"]) ?? "" };
            var reply = await GttPostAsync(session, "cancelRule", body, cancellationToken);
            if (!GttOk(reply)) return GttResult.Refused(GttStatus(reply), GttMessage(reply, $"Failed to cancel GTT rule {ruleId}") + Done());
            cancelled.Add(RuleId(reply) ?? ruleId);
        }

        return GttResult.Success(string.Join('-', cancelled));
    }

    public async Task<JsonArray> GetGttBookAsync(BrokerSession session, bool includeHistory, CancellationToken cancellationToken)
    {
        // Only the states that can still fire are asked for, so the book is active-only whatever includeHistory says.
        var rules = new List<JsonObject>();
        for (var page = 1; page <= RuleListMaxPages; page++)
        {
            var body = new JsonObject { ["status"] = new JsonArray(ActiveGttStatuses.Select(x => (JsonNode?)x).ToArray()), ["page"] = page, ["count"] = RuleListPageSize };
            var reply = await GttPostAsync(session, "ruleList", body, cancellationToken);
            if (!GttOk(reply))
            {
                // A partial listing beats none.
                if (page > 1 && rules.Count > 0) break;
                throw new BrokerException(GttMessage(reply, "Failed to fetch GTT book"), GttStatus(reply));
            }

            var rows = reply!["data"] switch { JsonObject one => [one], JsonArray many => many.OfType<JsonObject>().ToList(), _ => new List<JsonObject>() };
            rules.AddRange(rows);
            if (rows.Count < RuleListPageSize) break;
        }

        var book = new JsonArray();
        foreach (var rule in rules)
        {
            var status = (Str(rule["status"]) ?? "").ToUpperInvariant();
            if (!ActiveGttStatuses.Contains(status)) continue;
            var exchange = Str(rule["exchange"]) ?? "";
            var brokerSymbol = Str(rule["tradingsymbol"]) ?? "";
            var record = brokerSymbol.Length > 0 && exchange.Length > 0 ? await symbols.FindByBrokerSymbolAsync(Name, brokerSymbol, exchange, cancellationToken) : null;
            book.Add(new JsonObject
            {
                ["trigger_id"] = Str(rule["id"]) is { Length: > 0 } id ? id : Str(rule["gttid"]) is { Length: > 0 } g ? g : Str(rule["ruleid"]) ?? "",
                ["trigger_type"] = "single",
                ["status"] = status is "NEW" or "ACTIVE" or "SENTTOEXCHANGE" ? "active" : status.ToLowerInvariant(),
                ["symbol"] = record?.Symbol is { Length: > 0 } oa ? oa : brokerSymbol,
                ["exchange"] = exchange,
                ["trigger_prices"] = new JsonArray(Float(rule["triggerprice"])),
                ["last_price"] = 0,
                ["legs"] = new JsonArray(new JsonObject
                {
                    ["action"] = (Str(rule["transactiontype"]) ?? "").ToUpperInvariant(),
                    ["quantity"] = (int)Float(rule["qty"]),
                    ["price"] = Float(rule["price"]),
                    ["pricetype"] = "LIMIT",
                    ["product"] = BookGttProduct(Str(rule["producttype"])),
                }),
                ["created_at"] = Str(rule["createddate"]) ?? "",
                ["updated_at"] = Str(rule["updateddate"]) ?? "",
                ["expires_at"] = Str(rule["expirydate"]) ?? "",
            });
        }

        return book;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>A GTT accepts only DELIVERY and MARGIN, so NRML (CARRYFORWARD in orders) is sent as MARGIN.</summary>
    private static string GttProduct(string product) => product == "CNC" ? "DELIVERY" : "MARGIN";

    private static string BookGttProduct(string? product) => (product ?? "").ToUpperInvariant() switch
    {
        "MARGIN" or "CARRYFORWARD" => "NRML", "INTRADAY" => "MIS", "DELIVERY" => "CNC", _ => "CNC",
    };

    /// <summary>Angel sends every number in a GTT as text, and writes a whole number without a decimal tail.</summary>
    private static string Fmt(decimal value) => Fmt((double)value);

    private static string Fmt(double value) => value == Math.Floor(value) && Math.Abs(value) < 1e15 ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The legs of a rule set: the stoploss leg first, so the ids in a composite trigger id always read stoploss-target.</summary>
    private static List<(string Label, decimal Trigger, decimal Limit)> GttLegs(GttOrder order) =>
        order.IsOco ? [("SL", order.TriggerSl, order.StopLoss), ("TG", order.TriggerTg, order.Target)] : [("SINGLE", order.Trigger, order.Price)];

    private static List<string> SplitRuleIds(string triggerId) => triggerId.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool GttOk(JsonObject? reply) => reply?["status"] is JsonValue v && v.TryGetValue<bool>(out var ok) && ok;

    private static string GttMessage(JsonObject? reply, string fallback) => Str(reply?["message"]) is { Length: > 0 } m ? m : fallback;

    private static int GttStatus(JsonObject? reply) => Str(reply?["errorcode"]) is { } code && GttErrorStatus.TryGetValue(code, out var status) ? status : 400;

    private static string? RuleId(JsonObject? reply) => Str(reply?["data"]?["id"]) is { Length: > 0 } id ? id : null;

    private async Task<JsonObject?> GttPostAsync(BrokerSession session, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = Request(session, HttpMethod.Post, $"{GttBase}/{path}", body);
        using var response = await SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken) as JsonObject;
    }

    /// <summary>A rule that was created before its pair failed is cancelled, so Angel is never left holding half of a two-leg trigger.</summary>
    private async Task RollBackAsync(BrokerSession session, List<string> created, GttOrder order, CancellationToken cancellationToken)
    {
        foreach (var id in created)
        {
            logger.LogWarning("Angel GTT: cancelling rule {RuleId} because its pair could not be created.", id);
            await GttPostAsync(session, "cancelRule", new JsonObject { ["id"] = id, ["symboltoken"] = order.Instrument.Token, ["exchange"] = order.Instrument.Exchange }, cancellationToken);
        }
    }

    private sealed record ProtectedOrder(GttOrder? Order, GttResult? Refusal);

    /// <summary>
    /// A rule always fires a LIMIT order, so a MARKET request is turned into a LIMIT a protection slab beyond the price: around the last
    /// price for a SINGLE, around each trigger for an OCO (which needs no quote). Only a MARKET SINGLE reads the last price.
    /// </summary>
    private async Task<ProtectedOrder> ProtectedMarketAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken)
    {
        if (order.PriceType != "MARKET") return new ProtectedOrder(order, null);
        var instrument = order.Instrument;
        var type = MarketPriceProtection.InstrumentType(instrument.InstrumentType, instrument.Symbol);
        if (order.IsOco)
        {
            var stopLoss = order.TriggerSl > 0 ? (decimal)MarketPriceProtection.ProtectedPrice((double)order.TriggerSl, order.Action, type, instrument.TickSize) : order.StopLoss;
            var target = order.TriggerTg > 0 ? (decimal)MarketPriceProtection.ProtectedPrice((double)order.TriggerTg, order.Action, type, instrument.TickSize) : order.Target;
            return new ProtectedOrder(order with { StopLoss = stopLoss, Target = target, PriceType = "LIMIT" }, null);
        }

        double lastPrice;
        try
        {
            lastPrice = Float((await GetQuoteAsync(session, instrument.Symbol, instrument.Exchange, cancellationToken))["ltp"]);
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Angel GTT: the last price of {Symbol} could not be read.", instrument.Symbol);
            lastPrice = 0;
        }

        if (lastPrice == 0) return new ProtectedOrder(null, GttResult.Refused(502, "Failed to fetch last_price from Angel quotes"));
        return new ProtectedOrder(order with { Price = (decimal)MarketPriceProtection.ProtectedPrice(lastPrice, order.Action, type, instrument.TickSize), PriceType = "LIMIT" }, null);
    }
}
