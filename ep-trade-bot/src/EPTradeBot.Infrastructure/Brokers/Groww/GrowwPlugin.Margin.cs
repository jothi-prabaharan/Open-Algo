using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Groww;

/// <summary>
/// Groww margin (broker/groww api/margin_api.py): POST /v1/margins/detail/orders?segment=CASH|FNO with a list of order items. Groww works out a basket only for F&amp;O, so the F&amp;O legs go as one basket and each cash leg as a request of
/// its own, and the answers are added. A leg that cannot be sent to Groww refuses the whole basket, because margin for part of it would understate what the trader needs.
/// </summary>
public sealed partial class GrowwPlugin : IBrokerMargin
{
    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var cash = new List<JsonObject>();
        var fno = new List<JsonObject>();
        foreach (var leg in legs)
        {
            JsonObject item;
            string segment;
            try
            {
                segment = SegmentOf(leg.Instrument.Exchange);
                if (string.IsNullOrEmpty(leg.Instrument.BrokerSymbol)) throw new ArgumentException($"{leg.Instrument.Symbol} is not in the {leg.Instrument.Exchange} master contract");
                item = new JsonObject
                {
                    ["trading_symbol"] = leg.Instrument.BrokerSymbol, ["quantity"] = leg.Quantity, ["exchange"] = ExchangeOf(leg.Instrument.Exchange), ["segment"] = segment, ["product"] = ProductOf(leg.Product), ["order_type"] = OrderTypeOf(leg.PriceType),
                    ["transaction_type"] = SideOf(leg.Action),
                };
            }
            catch (ArgumentException ex)
            {
                throw new BrokerException($"Cannot calculate margin for {leg.Instrument.Symbol} on {leg.Instrument.Exchange}: {ex.Message}", 400);
            }

            if (leg.Price > 0) item["price"] = (double)leg.Price;
            (segment == "FNO" ? fno : cash).Add(item);
        }

        if (cash.Count == 0 && fno.Count == 0) throw new BrokerException("No positions to calculate margin for", 400);
        var requests = new List<(string Segment, List<JsonObject> Items)>();
        if (fno.Count > 0) requests.Add(("FNO", fno));
        requests.AddRange(cash.Select(c => ("CASH", new List<JsonObject> { c })));

        double total = 0, span = 0, exposure = 0;
        foreach (var (segment, items) in requests)
        {
            var (status, body, _) = await SendAsync(session, HttpMethod.Post, "/v1/margins/detail/orders", "non_trading", [KeyValuePair.Create("segment", segment)], new JsonArray(items.Select(i => (JsonNode?)i.DeepClone()).ToArray()), cancellationToken);
            if (body is not JsonObject answer) throw new BrokerException($"{Display} returned no margin data", status >= 400 ? status : 400);
            if (Str(answer["status"]) != "SUCCESS") throw new BrokerException(answer["error"] is JsonObject error && Truthy(error["message"]) ? Str(error["message"])! : $"{Display} could not calculate the margin", status >= 400 ? status : 400);
            var payload = Payload(answer);
            double Figure(string key) => !Truthy(payload[key]) ? 0 : BookFormatting.Number(payload[key]) ?? (double.TryParse(Str(payload[key]), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new BrokerException($"{Display} sent margin figures that could not be read. Try again in a moment.", 502));
            total += Figure("total_requirement");
            span += Figure("span_required");
            exposure += Figure("exposure_required");
        }

        return new JsonObject { ["total_margin_required"] = total, ["span_margin"] = span, ["exposure_margin"] = exposure };
    }
}
