using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Sahi;

/// <summary>
/// Sahi's margin calculator (broker/sahi/api/margin_api.py). One position goes to /v1/margins/order, two or more to /v1/margins/basket, so Sahi nets hedged legs against each
/// other; each leg is the body of a place-order call. A basket takes ten legs, so a larger request goes in consecutive baskets whose requirements are added. Sahi gives one
/// required figure with no SPAN and exposure split, so both are reported as zero.
/// </summary>
public sealed partial class SahiPlugin : IBrokerMargin
{
    private const int BasketLegCap = 10;

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var bodies = new List<JsonObject>();
        foreach (var l in legs)
        {
            var (payload, refusal) = await OrderPayloadAsync(l.Instrument.Exchange, l.Instrument.Symbol, l.Action, l.Quantity, l.PriceType, l.Product, l.Price, l.TriggerPrice, 0, cancellationToken);
            if (payload is null) throw new BrokerException(refusal!, 400);
            bodies.Add(payload);
        }

        if (bodies.Count == 0) throw new BrokerException("No positions to calculate margin for.", 400);
        var batches = bodies.Count == 1
            ? [("/v1/margins/order", bodies[0])]
            : bodies.Chunk(BasketLegCap).Select(chunk => ("/v1/margins/basket", new JsonObject { ["legs"] = new JsonArray(chunk.Select(b => (JsonNode?)b.DeepClone()).ToArray()) })).ToList();

        double required = 0, available = 0;
        foreach (var (path, body) in batches)
        {
            (int Status, JsonObject Body) reply;
            try
            {
                reply = await CallAsync(session, HttpMethod.Post, path, body, cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new BrokerException("Could not reach Sahi to calculate margin.", 500);
            }

            if (reply.Status != 200 || !reply.Body.ContainsKey("required_margin"))
            {
                var message = Str(reply.Body["code"]) == "UNDER_MAINTENANCE" ? "Sahi's margin calculator is closed for daily maintenance until 07:00 IST." : ErrorMessage(reply.Status, reply.Body);
                throw new BrokerException(message, reply.Status >= 400 ? reply.Status : 502);
            }

            required += FromPaise(reply.Body["required_margin"]);
            available = FromPaise(reply.Body["available_margin"]);
        }

        return new JsonObject { ["total_margin_required"] = BookFormatting.Round2(required), ["span_margin"] = 0.0, ["exposure_margin"] = 0.0, ["available_margin"] = available };
    }
}
