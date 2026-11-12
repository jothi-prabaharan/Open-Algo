using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Portfolio;

namespace EPTradeBot.Core.Options;

/// <summary>
/// The per-strategy position book: which strategy placed each order, and what each fill did to that strategy's leg. Weighted-average cost; a fill that carries a position through zero
/// realizes the part that closes and reopens the rest at the fill price. A fill is booked once however often it is reported, and a fill that arrives before its order's tag is held until the tag lands.
/// OpenAlgo's database.strategy_book_db, without the database.
/// </summary>
public sealed class StrategyBookLedger
{
    private sealed class Tag(string userId, string strategy, string symbol, string exchange, string product)
    {
        public string UserId { get; } = userId;
        public string Strategy { get; } = strategy;
        public string Symbol { get; } = symbol;
        public string Exchange { get; } = exchange;
        public string Product { get; } = product;
        public double AppliedQuantity { get; set; }
        public double AppliedNotional { get; set; }
    }

    private sealed class Leg(string userId, string strategy, string symbol, string exchange, string product)
    {
        public string UserId { get; } = userId;
        public string Strategy { get; } = strategy;
        public string Symbol { get; } = symbol;
        public string Exchange { get; } = exchange;
        public string Product { get; } = product;
        public double Quantity { get; set; }
        public double AveragePrice { get; set; }
        public double Realized { get; set; }
        public double TodayRealized { get; set; }
        public string TradeDate { get; set; } = "";
    }

    private readonly Dictionary<string, Tag> tags = [];
    private readonly List<Leg> legs = [];
    private readonly Dictionary<string, (double Quantity, double AveragePrice, string Action)> pending = [];

    /// <summary>Remembers which strategy placed an order. False when the order id or strategy is empty; a repeat is ignored. Any fill that came early is booked now.</summary>
    public bool RecordTag(string orderId, string userId, string strategy, string symbol, string exchange, string product, string today)
    {
        if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(strategy)) return false;
        if (tags.ContainsKey(orderId)) return true;
        tags[orderId] = new Tag(userId, strategy, symbol, exchange, product);
        if (pending.Remove(orderId, out var early)) ApplyFill(orderId, early.Quantity, early.AveragePrice, early.Action, today);
        return true;
    }

    /// <summary>Books the unseen part of a fill. Null when the order is unknown (the fill is held) or the fill adds nothing new.</summary>
    public JsonObject? ApplyFill(string orderId, double filledQuantity, double averagePrice, string action, string today)
    {
        if (!tags.TryGetValue(orderId, out var tag))
        {
            pending[orderId] = (filledQuantity, averagePrice, action);
            return null;
        }

        filledQuantity = Math.Abs(filledQuantity);
        var delta = filledQuantity - tag.AppliedQuantity;
        if (delta <= 0) return null;

        // The average price is cumulative over the whole order, so the price of this increment is the change in notional over the change in quantity.
        var cumulativeNotional = filledQuantity * averagePrice;
        var incrementalNotional = cumulativeNotional - tag.AppliedNotional;
        var price = delta != 0 ? incrementalNotional / delta : averagePrice;
        var signed = string.Equals(action, "BUY", StringComparison.OrdinalIgnoreCase) ? delta : -delta;

        var leg = legs.FirstOrDefault(l => l.UserId == tag.UserId && l.Strategy == tag.Strategy && l.Symbol == tag.Symbol && l.Exchange == tag.Exchange && l.Product == tag.Product);
        if (leg is null)
        {
            leg = new Leg(tag.UserId, tag.Strategy, tag.Symbol, tag.Exchange, tag.Product) { TradeDate = today };
            legs.Add(leg);
        }

        if (leg.TradeDate != today)
        {
            leg.TodayRealized = 0.0;
            leg.TradeDate = today;
        }

        var qty = leg.Quantity;
        var avg = leg.AveragePrice;
        if (qty == 0 || (qty > 0) == (signed > 0))
        {
            var total = Math.Abs(qty) + Math.Abs(signed);
            leg.AveragePrice = ((avg * Math.Abs(qty)) + (price * Math.Abs(signed))) / total;
            leg.Quantity = qty + signed;
        }
        else
        {
            var closing = Math.Min(Math.Abs(signed), Math.Abs(qty));
            var direction = qty > 0 ? 1.0 : -1.0;
            var realized = closing * (price - avg) * direction;
            leg.Realized += realized;
            leg.TodayRealized += realized;
            var remaining = Math.Abs(signed) - closing;
            leg.Quantity = qty + signed;
            if (Math.Abs(leg.Quantity) < 1e-9)
            {
                leg.Quantity = 0.0;
                leg.AveragePrice = 0.0;
            }
            else if (remaining > 0)
            {
                leg.AveragePrice = price;
            }
        }

        tag.AppliedQuantity = filledQuantity;
        tag.AppliedNotional = cumulativeNotional;
        return new JsonObject
        {
            ["strategy"] = leg.Strategy, ["symbol"] = leg.Symbol, ["exchange"] = leg.Exchange, ["product"] = leg.Product, ["quantity"] = PyNumber.Round(leg.Quantity, 4),
            ["average_price"] = PyNumber.Round(leg.AveragePrice, 4), ["realized_pnl"] = PyNumber.Round(leg.Realized, 4), ["today_realized_pnl"] = PyNumber.Round(leg.TodayRealized, 4), ["booked_quantity"] = PyNumber.Round(delta, 4),
        };
    }

    /// <summary>Every tracked leg in the shape the P&amp;L tool reads. A leg last traded on an earlier session reports no realized profit for today.</summary>
    public JsonArray Legs(string today, string? userId = null, string? strategy = null) => new(legs
        .Where(l => (userId is null || l.UserId == userId) && (strategy is null || l.Strategy == strategy))
        .Select(l => (JsonNode)new JsonObject
        {
            ["strategy"] = l.Strategy, ["symbol"] = l.Symbol, ["exchange"] = l.Exchange, ["product"] = l.Product, ["quantity"] = l.Quantity, ["average_price"] = l.AveragePrice,
            ["realized_pnl"] = l.Realized, ["today_realized_pnl"] = l.TradeDate == today ? l.TodayRealized : 0.0,
        }).ToArray());

    public static string SessionDate(DateTime nowIst) => (nowIst.TimeOfDay < TimeSpan.FromHours(3) ? nowIst.AddDays(-1) : nowIst).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
