using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Definedge;

/// <summary>
/// Definedge margin (broker/definedge api/margin_api.py, mapping/margin_data.py): one POST /spancalculator with every leg; the margin is the span plus the exposure. A leg carries the underlying's name, the expiry as DD-MMM-YYYY
/// (a two-digit year under 50 is 20xx, else 19xx), the strike as a whole number and, for an option, its type. A leg whose symbol is not in the master contract is refused by the platform by name, where OpenAlgo leaves it
/// out and understates the margin. A refusal is a failure status here; OpenAlgo answers 200 carrying it.
/// </summary>
public sealed partial class DefinedgePlugin
{
    private static JsonObject Leg(MarginLeg leg)
    {
        var info = leg.Instrument;
        var action = leg.Action.ToUpperInvariant();
        var body = new JsonObject
        {
            ["product_type"] = Product(leg.Product), ["exchange"] = leg.Instrument.Exchange, ["symbol_name"] = info.Name ?? "", ["tradingsymbol"] = info.BrokerSymbol,
            ["open_buy_qty"] = action == "BUY" ? leg.Quantity : 0, ["open_sell_qty"] = action == "SELL" ? leg.Quantity : 0,
        };
        var expiry = info.Expiry ?? "";
        if (expiry.Length > 0)
        {
            body["expiry"] = expiry.Length == 9 && char.IsAsciiDigit(expiry[7]) && char.IsAsciiDigit(expiry[8])
                ? $"{expiry[..7]}{(int.Parse(expiry[7..9], CultureInfo.InvariantCulture) < 50 ? "20" : "19")}{expiry[7..9]}"
                : expiry;
        }

        if (info.Strike is { } strike && strike != 0) body["option_strike"] = ((long)Math.Truncate(strike)).ToString(CultureInfo.InvariantCulture);
        if (info.InstrumentType is "CE" or "PE") body["option_type"] = info.InstrumentType;
        return body;
    }

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["positions"] = new JsonArray(legs.Select(l => (JsonNode?)Leg(l)).ToArray()) };
        var (_, answer) = await SendAsync(session, HttpMethod.Post, "/spancalculator", body, cancellationToken);
        if (answer is not JsonObject reply) throw new BrokerException($"{Display} sent a margin answer EP Trade bot could not read. Try again in a moment.", 500);
        var status = Str(reply["status"]);
        if (status != "SUCCESS") throw new BrokerException(Truthy(reply["message"]) ? Str(reply["message"])! : status is "FAILURE" or "ERROR" ? "Failed to calculate margin" : "Unknown error from broker", 400);
        foreach (var field in new[] { "span", "exposure" })
        {
            if (reply[field] is { } node && Str(node) is { } text && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new BrokerException($"{Display} sent a margin figure EP Trade bot could not read. Try again in a moment.", 400);
        }

        var span = reply["span"] is null ? 0 : Float(reply["span"]);
        var exposure = reply["exposure"] is null ? 0 : Float(reply["exposure"]);
        return new JsonObject { ["total_margin_required"] = span + exposure, ["span_margin"] = span, ["exposure_margin"] = exposure };
    }
}
