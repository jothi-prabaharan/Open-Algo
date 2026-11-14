using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Samco;

/// <summary>
/// Samco margin (broker/samco/api/margin_api.py, mapping/margin_data.py). One call, POST /spanMargin, takes every leg as a limit order with its price (zero when none) and answers with the total, the span and
/// the exposure margin and the spread benefit. Samco works out margin only for futures and options, so a leg on a cash exchange is refused by name, where OpenAlgo leaves it out and understates the margin.
/// </summary>
public sealed partial class SamcoPlugin : IBrokerMargin
{
    private static readonly HashSet<string> MarginExchanges = ["NFO", "MCX", "CDS", "BFO", "MFO"];

    private static string MarginProduct(string? product) => (product ?? "NRML").ToUpperInvariant() switch { "MIS" or "INTRADAY" => "MIS", "CNC" => "CNC", _ => "NRML" };

    /// <summary>safe_float(a) or safe_float(b): the second is read only when the first is missing or zero.</summary>
    private static double FirstNonZero(JsonNode? first, JsonNode? second)
    {
        var value = SafeFloat(first);
        return value != 0 ? value : SafeFloat(second);
    }

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var request = new JsonArray();
        foreach (var leg in legs)
        {
            if (!MarginExchanges.Contains(leg.Instrument.Exchange))
                throw new BrokerException($"{DisplayName} works out margin only for futures and options, so {leg.Instrument.Symbol} on {leg.Instrument.Exchange} cannot be included. Remove it and try again.", 400);
            request.Add(new JsonObject
            {
                ["exchange"] = leg.Instrument.Exchange, ["tradingSymbol"] = leg.Instrument.BrokerSymbol, ["qty"] = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), ["productType"] = MarginProduct(leg.Product),
                ["orderType"] = "L", ["transactionType"] = leg.Action.ToUpperInvariant(), ["price"] = leg.Price > 0 ? PyFloat((double)leg.Price) : "0",
            });
        }

        var (status, body) = await SendAsync(session, HttpMethod.Post, "/spanMargin", new JsonObject { ["request"] = request }, cancellationToken);
        if (body.Count == 0) throw new BrokerException($"{DisplayName} sent a margin answer EP Trade bot could not read. Try again in a moment.", status is >= 400 ? status : 500);
        if (!Succeeded(body)) throw new BrokerException(Reason(body, "Failed to calculate margin"), 400);

        if (body["spanDetails"] is JsonObject { Count: > 0 } span)
        {
            return new JsonObject
            {
                ["total_margin_required"] = FirstNonZero(span["totalMargin"], span["totalRequirement"]), ["span_margin"] = FirstNonZero(span["marginRequired"], span["spanRequirement"]),
                ["exposure_margin"] = SafeFloat(span["exposureMargin"]), ["spread_benefit"] = SafeFloat(span["spreadBenefit"]),
            };
        }

        return new JsonObject
        {
            ["total_margin_required"] = FirstNonZero(body["totalMargin"], body["marginRequired"]), ["span_margin"] = SafeFloat(body["marginRequired"]), ["exposure_margin"] = SafeFloat(body["exposureMargin"]), ["spread_benefit"] = 0,
        };
    }
}
