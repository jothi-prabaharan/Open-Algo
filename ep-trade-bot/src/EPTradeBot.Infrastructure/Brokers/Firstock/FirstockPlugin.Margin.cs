using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Brokers.Gtt;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Firstock;

/// <summary>
/// Firstock margin (broker/firstock/api/margin_api.py, mapping/margin_data.py): one POST /basketMargin with the first leg flat at the top of the body and the rest in BasketList_Params. A market or stop-market leg is
/// converted to a limit or stop-limit leg a slab beyond the last price, and when no price can be had it is still converted, at the leg's own price (its trigger for a stop-market). The answer's traded margin is the total;
/// Firstock gives no span or exposure split. A leg whose symbol is not in the master contract is refused by name, where OpenAlgo leaves it out and understates the margin.
/// </summary>
public sealed partial class FirstockPlugin : IBrokerMargin
{
    private static string Whole(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<JsonObject> LegAsync(BrokerSession session, MarginLeg leg, CancellationToken cancellationToken)
    {
        var instrument = leg.Instrument;
        var action = leg.Action.ToUpperInvariant();
        string price = Whole(leg.Price), type;
        if (leg.PriceType is "MARKET" or "SL-M")
        {
            type = leg.PriceType == "MARKET" ? "LMT" : "SL-LMT";
            var fallback = leg.PriceType == "MARKET" ? Whole(leg.Price) : Whole(leg.TriggerPrice);
            price = fallback;
            try
            {
                var quote = await QuoteAsync(session, instrument, instrument.Exchange, cancellationToken);
                if (quote.Ltp > 0)
                {
                    double? tick = instrument.TickSize is { } t && t > 0 ? t : null;
                    price = PyFloat(MarketPriceProtection.ProtectedPrice(quote.Ltp, action, MarketPriceProtection.InstrumentType(null, instrument.Symbol), tick));
                }
            }
            catch (BrokerException)
            {
            }
        }
        else
        {
            type = leg.PriceType == "LIMIT" ? "LMT" : leg.PriceType == "SL" ? "SL-LMT" : "MKT";
        }

        return new JsonObject
        {
            ["exchange"] = instrument.Exchange, ["tradingSymbol"] = Escape(instrument.BrokerSymbol), ["quantity"] = leg.Quantity.ToString(CultureInfo.InvariantCulture), ["price"] = price,
            ["triggerPrice"] = Whole(leg.TriggerPrice), ["product"] = Product(string.IsNullOrEmpty(leg.Product) ? "NRML" : leg.Product), ["transactionType"] = action == "BUY" ? "B" : "S", ["priceType"] = type,
        };
    }

    public async Task<JsonObject> CalculateMarginAsync(BrokerSession session, IReadOnlyList<MarginLeg> legs, CancellationToken cancellationToken)
    {
        var built = new List<JsonObject>();
        foreach (var leg in legs) built.Add(await LegAsync(session, leg, cancellationToken));

        var body = (JsonObject)built[0].DeepClone();
        body["BasketList_Params"] = new JsonArray(built.Skip(1).Select(l => (JsonNode?)l).ToArray());
        var (_, answer) = await PostAsync(session, "/basketMargin", body, cancellationToken);
        if (answer.Count == 0) throw new BrokerException($"{DisplayName} sent a margin answer EP Trade bot could not read. Try again in a moment.", 500);
        if (!Succeeded(answer))
        {
            var said = Str((answer["error"] as JsonObject)?["message"]) is { Length: > 0 } m ? m : Str(answer["message"]) is { Length: > 0 } t ? t : Str(answer["emsg"]) is { Length: > 0 } e ? e : "Failed to calculate margin";
            throw new BrokerException(said, 400);
        }

        var traded = (answer["data"] as JsonObject)?["TradedMargin"];
        if (Truthy(traded) && Str(traded) is { } text && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            throw new BrokerException($"{DisplayName} sent a margin figure EP Trade bot could not read. Try again in a moment.", 400);
        return new JsonObject { ["total_margin_required"] = Truthy(traded) ? Num(traded) : 0.0, ["span_margin"] = 0, ["exposure_margin"] = 0 };
    }
}
