using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Brokers;

/// <summary>
/// The broker-agnostic formatting OpenAlgo's service layer applies to every book
/// (services/orderbook_service.py, tradebook_service.py, positionbook_service.py,
/// holdings_service.py). Ported rule for rule so every broker's books read the same.
/// </summary>
public static class BookFormatting
{
    private static readonly HashSet<string> OrderQuantityFields = new(StringComparer.Ordinal)
    {
        "quantity", "qty", "filledqty", "filled_quantity", "tradedqty", "traded_quantity",
        "pendingqty", "pending_quantity", "unfilledqty", "unfilled_quantity",
    };

    private static readonly HashSet<string> TradeQuantityFields = new(StringComparer.Ordinal)
    {
        "quantity", "qty", "tradedqty", "filledqty", "filled_quantity", "traded_quantity",
    };

    private static readonly HashSet<string> PositionQuantityFields = new(StringComparer.Ordinal)
    {
        "quantity", "qty", "netqty", "net_qty", "buyqty", "buy_quantity", "sellqty", "sell_quantity", "daybuyqty", "daysellqty",
    };

    /// <summary>Python's <c>round(x, 2)</c>: rounds the binary value half-to-even, so 2.675 becomes 2.67.</summary>
    public static double Round2(double value)
    {
        // G17 exposes the binary value (2.675 is 2.6749999999999998), which is what Python rounds.
        var exact = decimal.Parse(value.ToString("G17", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return (double)Math.Round(exact, 2, MidpointRounding.ToEven);
    }

    /// <summary>The numeric value of a JSON number node, or null for anything else (Python's isinstance(v, (int, float))).</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<JsonElement>(out var e)) return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    private static JsonNode? WholeOrSame(JsonNode? node, double value) =>
        value == Math.Truncate(value) ? JsonValue.Create((long)value) : node?.DeepClone();

    private static JsonArray MapRows(JsonArray rows, Func<string, JsonNode?, JsonNode?> map)
    {
        var result = new JsonArray();
        foreach (var row in rows)
        {
            if (row is not JsonObject obj)
            {
                result.Add(row?.DeepClone());
                continue;
            }

            var formatted = new JsonObject();
            foreach (var (key, value) in obj) formatted[key] = map(key, value);
            result.Add(formatted);
        }

        return result;
    }

    /// <summary>format_order_data: prices to 2 places, whole quantities as integers, price 0 for MARKET orders.</summary>
    public static JsonArray Orders(JsonArray orders)
    {
        var result = MapRows(orders, (key, value) => Number(value) is { } n
            ? OrderQuantityFields.Contains(key.ToLowerInvariant()) ? WholeOrSame(value, n) : JsonValue.Create(Round2(n))
            : value?.DeepClone());

        foreach (var row in result.OfType<JsonObject>())
        {
            var priceType = row["pricetype"] is JsonValue p && p.TryGetValue<string>(out var s) ? s : "";
            if (string.Equals(priceType, "MARKET", StringComparison.OrdinalIgnoreCase)) row["price"] = 0.0;
        }

        return result;
    }

    /// <summary>format_statistics for the order book: counts stay integers, other numbers to 2 places.</summary>
    public static JsonObject OrderStatistics(JsonObject statistics)
    {
        var result = new JsonObject();
        string[] counts = ["total_", "orders", "completed", "open", "rejected"];
        foreach (var (key, value) in statistics)
        {
            var n = Number(value);
            result[key] = counts.Any(key.Contains)
                ? n is { } c ? JsonValue.Create((long)Math.Truncate(c)) : value?.DeepClone()
                : n is { } d ? JsonValue.Create(Round2(d)) : value?.DeepClone();
        }

        return result;
    }

    /// <summary>format_trade_data.</summary>
    public static JsonArray Trades(JsonArray trades) =>
        MapRows(trades, (key, value) => Number(value) is { } n
            ? TradeQuantityFields.Contains(key.ToLowerInvariant()) ? WholeOrSame(value, n) : JsonValue.Create(Round2(n))
            : value?.DeepClone());

    /// <summary>format_position_data. lot_size keeps full precision (crypto lots can be 0.001).</summary>
    public static JsonArray Positions(JsonArray positions) =>
        MapRows(positions, (key, value) =>
        {
            var lower = key.ToLowerInvariant();
            if (lower == "lot_size") return value?.DeepClone();
            return Number(value) is { } n
                ? PositionQuantityFields.Contains(lower) ? WholeOrSame(value, n) : JsonValue.Create(Round2(n))
                : value?.DeepClone();
        });

    /// <summary>format_holdings_data: only pnl and pnlpercent are rounded.</summary>
    public static JsonArray Holdings(JsonArray holdings) =>
        MapRows(holdings, (key, value) => (key is "pnl" or "pnlpercent") && Number(value) is { } n ? JsonValue.Create(Round2(n)) : value?.DeepClone());

    /// <summary>format_statistics for holdings: every number to 2 places.</summary>
    public static JsonObject HoldingsStatistics(JsonObject statistics)
    {
        var result = new JsonObject();
        foreach (var (key, value) in statistics) result[key] = Number(value) is { } n ? JsonValue.Create(Round2(n)) : value?.DeepClone();
        return result;
    }

    /// <summary>Python's <c>f"{x:.2f}"</c> (half-to-even on the binary value).</summary>
    public static string Fixed2(double value) => Round2(value).ToString("F2", CultureInfo.InvariantCulture);
}
