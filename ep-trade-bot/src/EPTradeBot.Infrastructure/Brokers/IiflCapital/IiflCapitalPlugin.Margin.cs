using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IiflCapital;

/// <summary>
/// IIFL Capital margin (broker/iiflcapital/api/margin_api.py, mapping/margin_data.py). One call, POST /spanexposure, takes every leg as an instrument, exchange, side and quantity and answers with
/// the span and exposure margin; the total is the broker's own figure when it sends one (even an empty one, which reads as zero) and span plus exposure when it does not.
/// </summary>
public sealed partial class IiflCapitalPlugin : IBrokerMargin
{
    private static double Safe(JsonNode? node)
    {
        if (node is null || Str(node) is "") return 0.0;
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
    }

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var body = new JsonArray(legs.Select(l => (JsonNode?)new JsonObject
        {
            ["instrumentId"] = l.Instrument.Token, ["exchange"] = BrokerExchange(l.Instrument.Exchange), ["transactionType"] = l.Action.ToUpperInvariant(), ["quantity"] = l.Quantity,
        }).ToArray());

        var reply = await SendAsync(session, HttpMethod.Post, "/spanexposure", body, cancellationToken);
        var payload = reply.Body as JsonObject ?? [];
        if (reply.Status != 200 || (Str(payload["status"]) ?? "").Equals("error", StringComparison.OrdinalIgnoreCase))
            throw new BrokerException(Message(payload, "Failed to calculate margin"), reply.Status != 200 ? reply.Status : 400);

        var node = payload.ContainsKey("result") ? payload["result"] : payload;
        if (node is not JsonObject result) throw new BrokerException($"{DisplayName} sent a margin answer EP Trade bot could not read. Try again in a moment.", 502);

        var span = Safe(result["span"]);
        var exposure = Safe(result["exposureMargin"]);
        var total = result.ContainsKey("totalMargin") ? Safe(result["totalMargin"]) : span + exposure;
        return new JsonObject { ["total_margin_required"] = total, ["span_margin"] = span, ["exposure_margin"] = exposure, ["margin_benefit"] = 0.0 };
    }
}
