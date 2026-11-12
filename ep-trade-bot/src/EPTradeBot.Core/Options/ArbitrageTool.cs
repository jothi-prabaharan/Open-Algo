using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// The futures calendar-spread universe the arbitrage scanner watches: for each underlying, its three nearest monthly contracts and the near-next and near-third pairs. Only contract details are
/// returned; prices come live from the market-data feed. OpenAlgo's get_arbitrage_universe with the futures list passed in.
/// </summary>
public static class ArbitrageTool
{
    public const int MaxLegs = 3;
    public static readonly string[] DefaultExchanges = ["NFO", "MCX"];
    public static readonly string[] SupportedExchanges = ["NFO", "MCX", "BFO", "CDS"];

    /// <summary>The futures rows an exchange lists.</summary>
    public delegate IReadOnlyList<JsonObject> FuturesLookup(string exchange);

    private static DateTime ParseExpiry(string? expiry)
    {
        if (string.IsNullOrEmpty(expiry)) return DateTime.MaxValue;
        var text = expiry.Trim().ToUpperInvariant();
        foreach (var format in new[] { "dd-MMM-yy", "dd-MMM-yyyy" })
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return DateTime.MaxValue;
    }

    private static JsonObject Leg(JsonObject c) => new()
    {
        ["symbol"] = c["symbol"]?.DeepClone(), ["exchange"] = c["exchange"]?.DeepClone(), ["expiry"] = c["expiry"]?.DeepClone(), ["lotsize"] = c["lotsize"]?.DeepClone(), ["tick_size"] = c["tick_size"]?.DeepClone(),
    };

    private static List<(string Underlying, List<JsonObject> Contracts)> Nearest(IReadOnlyList<JsonObject> rows)
    {
        var order = new List<string>();
        var grouped = new Dictionary<string, List<(string Symbol, JsonObject Row)>>();
        foreach (var row in rows)
        {
            var symbol = (OptionChainReading.Text(row, "symbol") ?? string.Empty).ToUpperInvariant();
            if (!symbol.EndsWith("FUT", StringComparison.Ordinal)) continue;
            var underlying = OptionChainReading.Text(row, "name");
            var expiry = OptionChainReading.Text(row, "expiry");
            if (string.IsNullOrEmpty(underlying) || string.IsNullOrEmpty(expiry)) continue;
            if (!grouped.TryGetValue(underlying, out var list)) { grouped[underlying] = list = []; order.Add(underlying); }
            var at = list.FindIndex(x => x.Symbol == symbol);
            if (at >= 0) list[at] = (symbol, row); else list.Add((symbol, row));
        }

        return order.Select(u => (u, grouped[u].Select(x => x.Row).OrderBy(c => ParseExpiry(OptionChainReading.Text(c, "expiry"))).Take(MaxLegs).ToList())).ToList();
    }

    public static (bool Ok, JsonObject Response, int Code) Universe(IReadOnlyList<string> exchanges, FuturesLookup futures, DateTime now)
    {
        var requested = exchanges.Select(e => e.Trim().ToUpperInvariant()).Where(e => e.Length > 0).ToList();
        var scan = requested.Where(e => SupportedExchanges.Contains(e)).ToList();
        if (scan.Count == 0)
        {
            var shown = requested.Count > 0 ? "[" + string.Join(", ", requested.Select(r => $"'{r}'")) + "]" : "request";
            return (false, new JsonObject { ["status"] = "error", ["message"] = $"No supported exchanges in {shown}. Supported: {string.Join(", ", SupportedExchanges)}" }, 400);
        }

        var pairs = new JsonArray();
        var symbols = new Dictionary<string, JsonObject>();
        var order = new List<string>();
        var underlyingCount = 0;
        foreach (var exchange in scan)
            foreach (var (underlying, contracts) in Nearest(futures(exchange)))
            {
                if (contracts.Count < 2) continue;
                underlyingCount++;
                var near = contracts[0];
                foreach (var (type, far) in new[] { ("near-next", (JsonObject?)contracts[1]), ("near-third", contracts.Count > 2 ? contracts[2] : null) })
                {
                    if (far is null) continue;
                    pairs.Add(new JsonObject { ["id"] = $"{exchange}:{underlying}:{type}", ["underlying"] = underlying, ["exchange"] = exchange, ["type"] = type, ["near"] = Leg(near), ["far"] = Leg(far) });
                    foreach (var leg in new[] { near, far })
                    {
                        var key = $"{OptionChainReading.Text(leg, "exchange")}:{OptionChainReading.Text(leg, "symbol")}";
                        if (!symbols.ContainsKey(key)) order.Add(key);
                        symbols[key] = new JsonObject { ["symbol"] = leg["symbol"]?.DeepClone(), ["exchange"] = leg["exchange"]?.DeepClone() };
                    }
                }
            }

        var list = new JsonArray();
        foreach (var key in order) list.Add(symbols[key].DeepClone());
        var micro = now.Ticks % TimeSpan.TicksPerSecond / 10;
        var generated = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (micro > 0 ? "." + micro.ToString("D6", CultureInfo.InvariantCulture) : string.Empty);
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"Found {pairs.Count} calendar pairs across {list.Count} futures in {string.Join(", ", scan)}",
            ["data"] = new JsonObject
            {
                ["pairs"] = pairs, ["symbols"] = list, ["counts"] = new JsonObject { ["underlyings"] = underlyingCount, ["pairs"] = pairs.Count, ["symbols"] = list.Count }, ["generated_at"] = generated,
            },
        }, 200);
    }
}
