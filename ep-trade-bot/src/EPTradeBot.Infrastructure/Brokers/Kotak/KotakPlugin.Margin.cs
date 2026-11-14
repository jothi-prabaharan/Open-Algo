using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Kotak;

/// <summary>
/// Kotak margin (broker/kotak api/margin_api.py, mapping/margin_data.py): Kotak takes one order at a time, so a basket is one POST /quick/user/check-margin a leg and the required margins are added. OpenAlgo leaves a leg
/// that Kotak refuses out of the total and still reports a figure, which understates the margin; here every leg is asked and any refusal is the answer. A leg whose symbol is not in the master contract is refused by the platform by name.
/// </summary>
public sealed partial class KotakPlugin
{
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var neo = Parse(session);
        double total = 0;
        BrokerException? refusal = null;
        foreach (var leg in legs)
        {
            var body = new JsonObject
            {
                ["brkName"] = "KOTAK", ["brnchId"] = "ONLINE", ["exSeg"] = Segment(leg.Instrument.Exchange), ["prc"] = Whole(leg.Price), ["prcTp"] = OrderType(leg.PriceType),
                ["prod"] = leg.Product is "CNC" or "NRML" or "MIS" ? leg.Product : null, ["qty"] = leg.Quantity.ToString(CultureInfo.InvariantCulture), ["tok"] = leg.Instrument.Token,
                ["trnsTp"] = leg.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "B" : "S",
            };
            var (_, answer) = await FormAsync(neo, "/quick/user/check-margin", body, cancellationToken);
            if (answer is not JsonObject reply)
            {
                refusal ??= new BrokerException($"{Display} sent a margin answer EP Trade bot could not read. Try again in a moment.", 500);
                continue;
            }

            if (Str(reply["stat"]) != "Ok")
            {
                refusal ??= new BrokerException(Str(reply["errMsg"]) is { Length: > 0 } m ? m : "Failed to calculate margin", 400);
                continue;
            }

            var required = reply["reqdMrgn"];
            if (required is not null && Str(required) is { } text && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                refusal ??= new BrokerException($"{Display} sent a margin figure EP Trade bot could not read. Try again in a moment.", 400);
                continue;
            }

            total += required is null ? 0 : Float(required);
        }

        if (refusal is not null) throw refusal;
        return new JsonObject { ["total_margin_required"] = total, ["span_margin"] = 0, ["exposure_margin"] = 0 };
    }
}
