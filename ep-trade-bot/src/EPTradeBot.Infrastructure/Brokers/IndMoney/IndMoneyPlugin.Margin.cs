using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IndMoney;

/// <summary>
/// IndMoney margin (broker/indmoney/api/margin_api.py): the call takes one order, so each leg goes as GET /margin with a JSON body and the margins of the legs that worked are added up. A leg that failed (an error answer, a reply that is not JSON, a
/// number that is not a number) is left out of the sum, and only when every leg failed is the margin refused, naming the securities. A leg whose token is not a number is not sent at all.
/// </summary>
public sealed partial class IndMoneyPlugin : IBrokerMargin
{
    private static string MarginExchange(string exchange) => exchange switch { "NSE" => "NSE", "BSE" => "BSE", "NFO" => "NSE", "BFO" => "BSE", "CDS" => "NSE", "BCD" => "BSE", "MCX" => "MCX", _ => "NSE" };

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var bodies = new List<JsonObject>();
        foreach (var leg in legs)
        {
            var token = leg.Instrument.Token.Trim();
            if (token.Length == 0 || token.Equals("none", StringComparison.OrdinalIgnoreCase) || !token.Replace(".", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).All(char.IsAsciiDigit)) continue;
            bodies.Add(new JsonObject
            {
                ["segment"] = Segment(leg.Instrument.Exchange), ["txnType"] = leg.Action.ToUpperInvariant(), ["quantity"] = leg.Quantity.ToString(CultureInfo.InvariantCulture),
                ["price"] = leg.Price.ToString(CultureInfo.InvariantCulture), ["product"] = Product(leg.Product), ["securityID"] = token, ["exchange"] = MarginExchange(leg.Instrument.Exchange),
            });
        }

        if (bodies.Count == 0) throw new BrokerException("None of the positions could be priced for margin. Check that the symbols are valid.", 400);

        double total = 0, span = 0, exposure = 0;
        var worked = 0;
        var failed = new List<string>();
        foreach (var body in bodies)
        {
            var id = Str(body["securityID"]) ?? "";
            try
            {
                var (_, reply, _) = await SendAsync(session, HttpMethod.Get, "/margin", null, body, cancellationToken);
                if (reply is not JsonObject answer || Str(answer["status"]) == "error") { failed.Add(id); continue; }
                JsonObject data;
                if (!answer.ContainsKey("data")) data = [];
                else if (answer["data"] is JsonObject d) data = d;
                else { failed.Add(id); continue; }

                // Python adds the three one after another, so a number that is not one stops the leg after the ones before it were added.
                var ok = true;
                foreach (var (field, apply) in new (string, Action<double>)[] { ("total_margin", v => total += v), ("span_margin", v => span += v), ("exposure_margin", v => exposure += v) })
                {
                    if (!data.ContainsKey(field)) continue;
                    if (data[field] is JsonValue value && (value.TryGetValue<double>(out var number) || value.TryGetValue<long>(out var whole) && (number = whole) == whole)) apply(number);
                    else { ok = false; break; }
                }

                if (ok) worked++;
                else failed.Add(id);
            }
            catch (BrokerException)
            {
                failed.Add(id);
            }
        }

        if (worked == 0) throw new BrokerException($"{Display} could not work out the margin for {string.Join(", ", failed)}. Check the symbols, then try again.", 400);
        return new JsonObject { ["total_margin_required"] = total, ["span_margin"] = span, ["exposure_margin"] = exposure };
    }
}
