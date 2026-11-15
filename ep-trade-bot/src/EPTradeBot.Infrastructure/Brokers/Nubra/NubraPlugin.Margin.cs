using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>
/// Nubra margin (broker/nubra api/margin_api.py): one POST /sentinel/orders/funds_required with every leg as an order item and requestType NEW. The funds required are the total (charges included), else the margin; Nubra gives no split, so the margin is the span
/// part and the exposure part is nothing. A leg whose token is not a number is left out, and when none is left nothing is asked.
/// </summary>
public sealed partial class NubraPlugin : IBrokerMargin
{
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var orders = new JsonArray();
        foreach (var leg in legs)
        {
            var token = leg.Instrument.Token.Trim();
            if (token.Length == 0 || !token.Replace(".", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).All(char.IsAsciiDigit)) continue;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var reference)) continue;
            var item = new JsonObject
            {
                ["refId"] = (long)reference, ["qty"] = leg.Quantity, ["side"] = Side(leg.Action), ["deliveryType"] = Delivery(leg.Product), ["priceType"] = PriceType(leg.PriceType), ["validityType"] = Validity(leg.PriceType),
                ["isMultiLeg"] = false, ["executionMode"] = "ENTRY", ["stratTags"] = new JsonArray("openalgo-margin"),
            };
            if (PriceType(leg.PriceType) == "LIMIT") item["entryPrice"] = Paise(leg.Price);
            if (EntryTrigger(leg.PriceType, leg.TriggerPrice, leg.Action) is { } entry) item["entryConfig"] = entry;
            orders.Add(item);
        }

        if (orders.Count == 0) throw new BrokerException("None of the positions could be priced for margin. Check that the symbols are valid.", 400);
        var (status, body, _) = await SendAsync(session, HttpMethod.Post, "/sentinel/orders/funds_required", new JsonObject { ["requestType"] = "NEW", ["orders"] = orders }, true, cancellationToken);
        if (status == SessionExpired) throw new BrokerException(ExpiredMessage, 401);
        if (body is not JsonObject answer) throw new BrokerException($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
        if (answer["marginInfo"] is not JsonObject info) throw new BrokerException($"{Display} could not work out the margin: {MessageOrErrorFirst(answer) ?? "no reason was given."}", 400);
        double Number(JsonNode? node) => !Truthy(node) ? 0 : BookFormatting.Number(node) ?? (double.TryParse(Str(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new BrokerException($"{Display} sent margin figures that could not be read. Try again in a moment.", 502));
        var margin = Number(info["totalMargin"]);
        var required = Number(answer["totalFundsRequired"]);
        var charges = answer["brokerageInfo"] is JsonObject brokerage ? Number(brokerage["totalChargesFloat"]) : 0;
        return new JsonObject { ["total_margin_required"] = required != 0 ? required : margin, ["span_margin"] = margin, ["exposure_margin"] = 0.0, ["total_charges"] = BookFormatting.RoundPy(charges, 2) };
    }

    private static string? MessageOrErrorFirst(JsonObject answer) => Truthy(answer["error"]) ? Str(answer["error"]) : Truthy(answer["message"]) ? Str(answer["message"]) : null;
}
