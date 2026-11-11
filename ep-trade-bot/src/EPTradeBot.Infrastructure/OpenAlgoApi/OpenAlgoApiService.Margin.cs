using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>/margin, ported from services/margin_service.py: the margin a basket of positions needs, asked of the account's broker.</summary>
public sealed partial class OpenAlgoApiService
{
    /// <summary>
    /// The checks OpenAlgo makes on a basket before it asks the API key anything, in its order: at most 50 positions (the schema),
    /// then each position's quantity and prices. Null when the basket is fine.
    /// </summary>
    public static V1Result? ValidateMargin(MarginCalculatorRequest r)
    {
        var index = 0;
        foreach (var p in r.Positions)
        {
            index++;
            if (!int.TryParse(p.Quantity.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)) return V1Result.Error(400, $"Position {index}: Invalid quantity format");
            if (quantity <= 0) return V1Result.Error(400, $"Position {index}: Quantity must be a positive number");
            if (!decimal.TryParse(p.Price.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price)) return V1Result.Error(400, $"Position {index}: Invalid price format");
            if (price < 0) return V1Result.Error(400, $"Position {index}: Price cannot be negative");
            if (!decimal.TryParse(p.TriggerPrice.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return V1Result.Error(400, $"Position {index}: Invalid trigger price format");
        }

        return null;
    }

    public async Task<V1Result> MarginAsync(BrokerAccount account, MarginCalculatorRequest r, CancellationToken ct)
    {
        if (!Broker<IBrokerMargin>(account, out var margin, out var session, out var missing)) return missing!;

        // OpenAlgo leaves a position whose symbol it cannot find out of the basket and calculates the rest, which understates the margin.
        // EP Trade bot names the symbol instead.
        var legs = new List<MarginLeg>();
        foreach (var p in r.Positions)
        {
            var exchange = p.Exchange.ToUpperInvariant();
            if (await symbols.FindAsync(margin!.Name, p.Symbol, exchange, ct) is not { } instrument)
                return V1Result.Error(400, $"{p.Symbol} on {exchange} is not in the {margin.DisplayName} master contract, so the margin cannot be worked out. Check the symbol, or download the master contract again.");
            legs.Add(new MarginLeg(instrument, p.Action.ToUpperInvariant(), int.Parse(p.Quantity.Trim(), CultureInfo.InvariantCulture), p.Product, p.PriceType,
                decimal.Parse(p.Price.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture), decimal.Parse(p.TriggerPrice.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)));
        }

        return await Guarded(async () => new V1Result(200, new JsonObject { ["status"] = "success", ["data"] = await margin!.CalculateMarginAsync(session!, legs, ct) }));
    }
}
