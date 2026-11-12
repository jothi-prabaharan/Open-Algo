using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// Realized, unrealized and total profit per strategy. The broker nets positions with no strategy label, so a separate book keeps each strategy's legs; realized comes from that book and
/// unrealized is worked out here by marking open quantity to the position book's last price. OpenAlgo's pnl_from_book and get_strategy_pnl.
/// </summary>
public static class StrategyPnlTool
{
    /// <summary>The strategy book could not be read. An unreadable book is unknown, not empty.</summary>
    public sealed class BookUnavailableException(string message) : Exception(message);

    private static double F(JsonNode? node, double fallback = 0.0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<bool>(out var b)) return b ? 1.0 : 0.0;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    private static string? Text(JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Truthy(JsonNode? n) => n is JsonValue v && ((v.TryGetValue<string>(out var s) && s.Length > 0) || (v.TryGetValue<double>(out var d) && d != 0) || (v.TryGetValue<long>(out var l) && l != 0) || (v.TryGetValue<bool>(out var b) && b));

    private static JsonObject NewEntry(string name) => new()
    {
        ["strategy"] = name, ["realized"] = 0.0, ["today_realized"] = 0.0, ["unrealized"] = 0.0, ["total"] = 0.0, ["open_quantity"] = 0.0, ["unpriced_legs"] = 0, ["legs"] = new JsonArray(),
    };

    /// <summary>One strategy's figures when <paramref name="strategy"/> is named, otherwise every strategy keyed by name.</summary>
    public static JsonObject FromBook(JsonArray legs, JsonArray? positions, string? strategy)
    {
        var ltpByKey = new Dictionary<(string?, string?, string?), double>();
        foreach (var p in positions ?? [])
        {
            var raw = p is JsonObject po && po["ltp"] is not null ? po["ltp"] : (p as JsonObject)?["last_price"];
            ltpByKey[(Text(p, "symbol"), Text(p, "exchange"), Text(p, "product"))] = F(raw);
        }

        var order = new List<string>();
        var grouped = new Dictionary<string, (JsonObject Entry, double Realized, double TodayRealized, double Unrealized, double Open, int Unpriced, List<JsonObject> Legs)>();
        foreach (var leg in legs)
        {
            var name = Truthy((leg as JsonObject)?["strategy"]) ? Text(leg, "strategy") ?? "untagged" : "untagged";
            if (!string.IsNullOrEmpty(strategy) && name != strategy) continue;
            if (!grouped.TryGetValue(name, out var g))
            {
                g = (NewEntry(name), 0, 0, 0, 0, 0, []);
                order.Add(name);
            }

            var qty = F((leg as JsonObject)?["quantity"]);
            var avg = F((leg as JsonObject)?["average_price"]);
            var realized = F((leg as JsonObject)?["realized_pnl"]);
            var todayRealized = F((leg as JsonObject)?["today_realized_pnl"]);
            var key = (Text(leg, "symbol"), Text(leg, "exchange"), Text(leg, "product"));
            double? ltp = ltpByKey.TryGetValue(key, out var found) ? found : null;
            var unrealized = 0.0;
            if (Math.Abs(qty) > 1e-9)
            {
                if (ltp is null) g.Unpriced++;
                else unrealized = qty * (ltp.Value - avg);
                g.Open += qty;
            }

            g.Realized += realized;
            g.TodayRealized += todayRealized;
            g.Unrealized += unrealized;
            g.Legs.Add(new JsonObject
            {
                ["symbol"] = (leg as JsonObject)?["symbol"]?.DeepClone(), ["exchange"] = (leg as JsonObject)?["exchange"]?.DeepClone(), ["product"] = (leg as JsonObject)?["product"]?.DeepClone(),
                ["quantity"] = PyNumber.Round(qty, 4), ["average_price"] = PyNumber.Round(avg, 4), ["ltp"] = ltp, ["realized"] = PyNumber.Round(realized, 4),
                ["today_realized"] = PyNumber.Round(todayRealized, 4), ["unrealized"] = PyNumber.Round(unrealized, 4),
            });
            grouped[name] = g;
        }

        var result = new JsonObject();
        foreach (var name in order)
        {
            var g = grouped[name];
            // Open legs first, insertion order kept within each group: a flow addresses a leg by position, and a flat leg's average price has been reset to zero.
            var sorted = g.Legs.Select((l, i) => (Leg: l, Index: i)).OrderBy(x => Math.Abs(OptionChainReading.Num(x.Leg, "quantity")) <= 1e-9).ThenBy(x => x.Index).Select(x => x.Leg).ToList();
            var realized = PyNumber.Round(g.Realized, 4);
            var today = PyNumber.Round(g.TodayRealized, 4);
            var unrealized = PyNumber.Round(g.Unrealized, 4);
            var entry = g.Entry;
            entry["realized"] = realized;
            entry["today_realized"] = today;
            entry["unrealized"] = unrealized;
            entry["total"] = PyNumber.Round(realized + unrealized, 4);
            entry["today_total"] = PyNumber.Round(today + unrealized, 4);
            entry["open_quantity"] = PyNumber.Round(g.Open, 4);
            entry["unpriced_legs"] = g.Unpriced;
            entry["legs"] = StrategyChartSupport.ToArray(sorted);
            result[name] = entry;
        }

        if (!string.IsNullOrEmpty(strategy))
        {
            if (result[strategy] is JsonObject one) return (JsonObject)one.DeepClone();
            var empty = NewEntry(strategy);
            empty["today_total"] = 0.0;
            return empty;
        }

        return result;
    }

    public delegate JsonArray LegsLookup();

    /// <summary>The strategy's (or every strategy's) figures from the persisted book and one position book call, or an error saying why the figures cannot be trusted.</summary>
    public static JsonObject Get(LegsLookup legs, Func<JsonObject?> positionBook, string? strategy)
    {
        JsonArray book;
        try
        {
            book = legs();
        }
        catch (BookUnavailableException e)
        {
            return new JsonObject { ["status"] = "error", ["message"] = $"Strategy book unavailable: {e.Message}" };
        }

        var response = positionBook() is { Count: > 0 } r ? r : new JsonObject();
        if (Text(response, "status") == "error")
        {
            var message = Truthy(response["error"]) ? Text(response, "error") : Truthy(response["message"]) ? Text(response, "message") : null;
            return new JsonObject { ["status"] = "error", ["message"] = $"Position book unavailable, cannot value open legs: {message ?? "unavailable"}" };
        }

        var positions = response["data"] as JsonArray ?? [];
        var result = FromBook(book, positions, strategy);
        if (!string.IsNullOrEmpty(strategy))
        {
            var one = new JsonObject { ["status"] = "success" };
            foreach (var (k, v) in result) one[k] = v?.DeepClone();
            return one;
        }

        return new JsonObject { ["status"] = "success", ["strategies"] = result.DeepClone(), ["count"] = result.Count };
    }
}
