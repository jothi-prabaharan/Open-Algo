using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Core.Portfolio;

/// <summary>One position read from a broker: weights come from quantity times last price, so those are the two fields a row must have.</summary>
public sealed record HeldPosition(string Symbol, string Exchange, double Quantity, double AveragePrice, double LastPrice, double Pnl, string Product)
{
    public double Invested => Quantity * AveragePrice;

    public double Current => Quantity * LastPrice;
}

/// <summary>Reading a broker's holdings and summarising them by current value. OpenAlgo's portfolio.holdings.</summary>
public static class PortfolioHoldings
{
    private static string PyStr(JsonNode? n) => n is null ? "None" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n is JsonValue b && b.TryGetValue<bool>(out var t) ? (t ? "True" : "False") : n.ToJsonString();

    /// <summary>A broker numeric, which may be a string or absent; anything unreadable is 0.</summary>
    private static double Number(JsonNode? value)
    {
        if (value is null) return 0.0;
        if (value is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
            {
                s = s.Trim();
                if (s is "" or "-") return 0.0;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.0;
            }

            if (v.TryGetValue<bool>(out var b)) return b ? 1.0 : 0.0;
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
        }

        return 0.0;
    }

    private static bool Falsy(JsonNode? n) => n is null || (n is JsonValue v && ((v.TryGetValue<string>(out var s) && s.Length == 0) || (v.TryGetValue<bool>(out var b) && !b) || (!v.TryGetValue<string>(out _) && Number(n) == 0)));

    public static List<HeldPosition> Parse(IEnumerable<JsonObject> rows)
    {
        var result = new List<HeldPosition>();
        foreach (var row in rows)
        {
            var symbol = row.ContainsKey("symbol") ? PyStr(row["symbol"]).Trim().ToUpperInvariant() : string.Empty;
            if (symbol.Length == 0) continue;
            var quantity = Number(row["quantity"]);
            var average = Number(row["average_price"]);
            var pnl = Number(row["pnl"]);
            var last = Number(Falsy(row["last_price"]) ? row["ltp"] : row["last_price"]);
            if (new[] { quantity, average, pnl, last }.Any(v => !double.IsFinite(v))) continue;
            if (quantity <= 0) continue;
            if (last <= 0 && average > 0) last = average + pnl / quantity;
            if (last <= 0) continue;
            var exchange = row.ContainsKey("exchange") ? PyStr(row["exchange"]).Trim().ToUpperInvariant() : "NSE";
            result.Add(new HeldPosition(symbol, exchange, quantity, average, last, pnl, row.ContainsKey("product") ? PyStr(row["product"]) : ""));
        }

        return result;
    }

    public static JsonObject Summarise(IReadOnlyList<HeldPosition> holdings)
    {
        if (holdings.Count == 0)
            return new JsonObject { ["holdings"] = new JsonArray(), ["weights"] = new JsonObject(), ["invested"] = 0.0, ["current"] = 0.0, ["pnl"] = 0.0, ["pnl_pct"] = 0.0, ["has_cost_basis"] = false, ["count"] = 0 };

        var invested = holdings.Sum(h => h.Invested);
        var current = holdings.Sum(h => h.Current);
        var hasCost = holdings.All(h => h.AveragePrice > 0);
        var rows = new List<(double Current, JsonObject Row)>();
        var weights = new JsonObject();
        foreach (var h in holdings)
        {
            var weight = current > 0 ? h.Current / current : 0.0;
            weights[h.Symbol] = weight * 100.0;
            rows.Add((PyNumber.Round(h.Current, 2), new JsonObject
            {
                ["symbol"] = h.Symbol, ["exchange"] = h.Exchange, ["quantity"] = h.Quantity, ["average_price"] = PyNumber.Round(h.AveragePrice, 2), ["last_price"] = PyNumber.Round(h.LastPrice, 2),
                ["invested"] = PyNumber.Round(h.Invested, 2), ["current"] = PyNumber.Round(h.Current, 2), ["pnl"] = PyNumber.Round(h.Pnl, 2),
                ["pnl_pct"] = h.Invested != 0 ? PyNumber.Round((h.Current / h.Invested - 1.0) * 100, 2) : null, ["weight"] = PyNumber.Round(weight, 5), ["product"] = h.Product,
            }));
        }

        return new JsonObject
        {
            ["holdings"] = new JsonArray(rows.Select((r, i) => (r, i)).OrderByDescending(x => x.r.Current).ThenBy(x => x.i).Select(x => (JsonNode)x.r.Row).ToArray()),
            ["weights"] = weights,
            ["invested"] = hasCost ? PyNumber.Round(invested, 2) : null,
            ["current"] = PyNumber.Round(current, 2),
            ["pnl"] = hasCost ? PyNumber.Round(current - invested, 2) : PyNumber.Round(holdings.Sum(h => h.Pnl), 2),
            ["pnl_pct"] = hasCost ? PyNumber.Round((current / invested - 1.0) * 100, 2) : null,
            ["has_cost_basis"] = hasCost,
            ["count"] = rows.Count,
        };
    }
}
