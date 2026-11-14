using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Arrow;

/// <summary>
/// Arrow's margin calculator (broker/arrow/api/margin_api.py). One position goes to /margin/order (the charges come with it), two or more to /margin/basket (with the open
/// positions netted in); a basket Arrow refuses is replaced by the sum of the single-order answers. Arrow gives one required figure with no SPAN and exposure split, so both are zero.
/// </summary>
public sealed partial class ArrowPlugin : IBrokerMargin
{
    private static double Figure(JsonNode? value) => BookFormatting.Number(value) is { } n ? n : Str(value) is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

    private static string MarginProduct(string product) => product.ToUpperInvariant() switch { "CNC" => "C", "MIS" => "I", "NRML" => "M", _ => "M" };

    private async Task<(int Status, JsonObject Body)> PostMarginAsync(BrokerSession session, string path, JsonNode body, CancellationToken cancellationToken)
    {
        var (status, reply) = await CallAsync(session, HttpMethod.Post, path, body, cancellationToken);
        return reply is JsonObject o ? (status, o) : throw new BrokerException("Arrow sent a reply EP Trade bot could not read. Try again in a moment.", 502);
    }

    /// <summary>Arrow's refusal as the failure it is; an error answered with an OK status is a 400.</summary>
    private static BrokerException MarginRefusal(int status, JsonObject payload) => new(Message(payload, "Failed to calculate margin"), status < 400 ? 400 : status);

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var orders = legs.Select(l => new JsonObject
        {
            ["exchange"] = l.Instrument.Exchange, ["symbol"] = l.Instrument.BrokerSymbol, ["quantity"] = l.Quantity.ToString(CultureInfo.InvariantCulture), ["product"] = MarginProduct(l.Product),
            ["price"] = l.Price.ToString(CultureInfo.InvariantCulture), ["transactionType"] = l.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "B" : "S", ["order"] = l.PriceType == "MARKET" ? "MKT" : "LMT",
        }).ToList();
        if (orders.Count == 0) throw new BrokerException("No valid positions to calculate margin. Check if symbols are valid.", 400);

        if (orders.Count == 1)
        {
            var (status, payload) = await PostMarginAsync(session, "/margin/order", orders[0], cancellationToken);
            if (Str(payload["status"]) != "success") throw MarginRefusal(status, payload);
            return Summed([payload["data"] as JsonObject ?? []]);
        }

        var (basketStatus, basket) = await PostMarginAsync(session, "/margin/basket", new JsonObject { ["orders"] = new JsonArray(orders.Select(o => (JsonNode?)o.DeepClone()).ToArray()), ["includePositions"] = true }, cancellationToken);
        if (Str(basket["status"]) == "success")
        {
            var data = basket["data"] as JsonObject ?? [];
            var charges = (data["orders"] as JsonArray ?? []).OfType<JsonObject>().Sum(o => Figure((o["charge"] as JsonObject)?["total"]));
            return new JsonObject
            {
                ["total_margin_required"] = BookFormatting.Round2(Figure(data["final_margin"])), ["span_margin"] = 0.0, ["exposure_margin"] = 0.0, ["total_charges"] = BookFormatting.Round2(charges),
            };
        }

        // The basket was refused: the sum of the single orders is the conservative answer.
        var answers = new List<JsonObject>();
        foreach (var order in orders)
        {
            var (status, payload) = await PostMarginAsync(session, "/margin/order", order, cancellationToken);
            if (Str(payload["status"]) != "success") throw MarginRefusal(status, payload);
            answers.Add(payload["data"] as JsonObject ?? []);
        }

        return Summed(answers);
    }

    private static JsonObject Summed(List<JsonObject> answers) => new()
    {
        ["total_margin_required"] = BookFormatting.Round2(answers.Sum(a => Figure(a["requiredMargin"]))), ["span_margin"] = 0.0, ["exposure_margin"] = 0.0,
        ["total_charges"] = BookFormatting.Round2(answers.Sum(a => Figure((a["charge"] as JsonObject)?["total"]))),
    };
}
