using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// 5paisa's stream (broker/fivepaisa/streaming): JSON over a WebSocket. A subscribe is a frame per method (MarketFeedV3 for prices, MarketDepthService for the ladder) listing up to fifty scrips; a depth subscriber needs both
/// methods, because the depth frame carries no price, and a method another subscription on the token already holds is neither sent again nor dropped until the last of them goes. A frame goes to every subscription on its
/// token, whichever method it came from, and for a price field that arrives as zero or not at all each mode holds the last non-zero value it saw.
/// </summary>
public sealed partial class FivePaisaFeedCodec(string? clientCode = null) : IFeedCodec
{
    private const int MaxPerFrame = 50;
    private const string Feed = "MarketFeedV3";
    private const string Ladder = "MarketDepthService";
    private static readonly string[] SnapshotFields = ["LastRate", "OpenRate", "High", "Low", "PClose", "BidRate", "OffRate", "AvgRate"];

    private sealed record Sub(string Symbol, string Exchange, StreamMode Mode, string Token, JsonObject Scrip, string[] Methods);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly Dictionary<string, Dictionary<string, JsonNode?>> lastSnapshot = [];
    private string? code = clientCode;

    public string Broker => "fivepaisa";

    public void Begin(EPTradeBot.Core.Brokers.BrokerSession session)
    {
        var parts = (session.Key("ApiKey") ?? "").Split(":::");
        if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])) lock (gate) code = parts[2].Trim();
    }

    private static string ExchangeCode(string brokerExchange) => brokerExchange.ToUpperInvariant() switch
    {
        "NSE" or "NFO" or "CDS" or "NSE_INDEX" => "N", "BSE" or "BFO" or "BSE_INDEX" => "B", "MCX" => "M", _ => "N",
    };

    private static string ExchangeType(string brokerExchange) => brokerExchange.ToUpperInvariant() switch
    {
        "NFO" or "BFO" or "MCX" => "D", "CDS" => "U", _ => "C",
    };

    private static string[] MethodsFor(StreamMode mode) => mode == StreamMode.Depth ? [Ladder, Feed] : [Feed];

    private static string Key(string symbol, string exchange, StreamMode mode) => $"{symbol}_{exchange}_{(int)mode}" + (mode == StreamMode.Depth ? "_5" : "");

    private static JsonObject ScripOf(string token, string brokerExchange) => new() { ["Exch"] = ExchangeCode(brokerExchange), ["ExchType"] = ExchangeType(brokerExchange), ["ScripCode"] = long.Parse(token, System.Globalization.CultureInfo.InvariantCulture) };

    private string Frame(string method, string operation, IEnumerable<JsonObject> scrips) => new JsonObject
    {
        ["Method"] = method, ["Operation"] = operation, ["ClientCode"] = code, ["MarketFeedData"] = new JsonArray(scrips.Select(s => (JsonNode?)s.DeepClone()).ToArray()),
    }.ToJsonString();

    /// <summary>The queued (method, scrip) pairs as frames: the method at the head goes first with every scrip of that method (fifty at most), then the next method, and so on.</summary>
    private List<string> Batches(List<(string Method, JsonObject Scrip)> queue)
    {
        var frames = new List<string>();
        while (queue.Count > 0)
        {
            var method = queue[0].Method;
            var batch = queue.Where(q => q.Method == method).Take(MaxPerFrame).ToList();
            foreach (var item in batch) queue.Remove(item);
            frames.Add(Frame(method, "Subscribe", batch.Select(b => b.Scrip)));
        }

        return frames;
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queue = new List<(string, JsonObject)>();
            foreach (var r in requests)
            {
                if (!long.TryParse(r.Token, out _)) continue;
                var methods = MethodsFor(r.Mode);
                var live = LiveMethods(r.Token);
                var scrip = ScripOf(r.Token, r.BrokerExchange);
                subs[Key(r.Symbol, r.Exchange, r.Mode)] = new Sub(r.Symbol, r.Exchange, r.Mode, r.Token, scrip, methods);
                foreach (var m in methods.Where(m => !live.Contains(m))) queue.Add((m, scrip));
            }

            return Batches(queue);
        }
    }

    private HashSet<string> LiveMethods(string token) => subs.Values.Where(s => s.Token == token).SelectMany(s => s.Methods).ToHashSet();

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            if (!long.TryParse(target.Token, out _)) return [];
            var frames = new List<string>();
            foreach (var m in mode is { } one ? [one] : Enum.GetValues<StreamMode>())
            {
                var methods = MethodsFor(m);
                var corr = $"{target.Symbol}_{target.Exchange}_{(int)m}";
                foreach (var k in subs.Keys.Where(k => k == corr || k.StartsWith(corr + "_", StringComparison.Ordinal)).ToList()) subs.Remove(k);
                var needed = LiveMethods(target.Token);
                if (!subs.Values.Any(s => s.Token == target.Token && s.Mode == m)) lastSnapshot.Remove($"{target.Token}_{(int)m}");
                var scrip = ScripOf(target.Token, target.BrokerExchange);
                foreach (var method in methods.Where(x => !needed.Contains(x))) frames.Add(Frame(method, "Unsubscribe", [scrip]));
            }

            return frames;
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var queue = new List<(string, JsonObject)>();
            var seen = new HashSet<(string, string)>();
            foreach (var sub in subs.Values)
            {
                foreach (var m in sub.Methods)
                {
                    if (seen.Add((m, sub.Token))) queue.Add((m, sub.Scrip));
                }
            }

            return Batches(queue);
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            lastSnapshot.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    // ---- reading -----------------------------------------------------------------------------------------------

    [GeneratedRegex(@"/Date\((\d+)")]
    private static partial Regex DatePattern();

    private static JsonNode? Held(JsonObject message, string name, JsonNode? fallback = null) => message.TryGetPropertyValue(name, out var value) ? value?.DeepClone() : fallback;

    private static bool IsZero(JsonNode? node) => node is null || (node is JsonValue v && v.TryGetValue<double>(out var d) && d == 0);

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(frame);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        var items = parsed switch { JsonArray list => list.ToList(), JsonObject one => [one], _ => [] };
        var published = new List<FeedPublish>();
        lock (gate)
        {
            foreach (var item in items)
            {
                if (item is JsonObject message) published.AddRange(OnData(message, nowMs));
            }
        }

        return published;
    }

    private List<FeedPublish> OnData(JsonObject message, long nowMs)
    {
        var token = message.TryGetPropertyValue("Token", out var t) && t is not null ? t is JsonValue v && v.TryGetValue<string>(out var s) ? s : t.ToJsonString() : "None";
        var published = new List<FeedPublish>();
        foreach (var sub in subs.Values.Where(s => s.Token == token).ToList())
        {
            var merged = Apply(message, $"{token}_{(int)sub.Mode}");
            var data = Normalize(merged, sub.Mode);
            data["symbol"] = sub.Symbol;
            data["exchange"] = sub.Exchange;
            data["mode"] = (int)sub.Mode;
            data["timestamp"] = nowMs;
            published.Add(new FeedPublish(MarketTopics.Format(sub.Exchange, sub.Symbol, sub.Mode), data));
        }

        return published;
    }

    /// <summary>A price field that arrives as zero, or not at all, takes the last non-zero value seen for the token in this mode; one that arrives non-zero becomes the value held.</summary>
    private JsonObject Apply(JsonObject message, string key)
    {
        if (!lastSnapshot.TryGetValue(key, out var last)) lastSnapshot[key] = last = [];
        var merged = (JsonObject)message.DeepClone();
        foreach (var field in SnapshotFields)
        {
            message.TryGetPropertyValue(field, out var current);
            if (IsZero(current))
            {
                if (last.TryGetValue(field, out var held) && !IsZero(held)) merged[field] = held!.DeepClone();
            }
            else
            {
                last[field] = current!.DeepClone();
            }
        }

        return merged;
    }

    private static JsonNode? Or(JsonObject message, string name) => Held(message, name, JsonValue.Create(0));

    private static long Time(JsonObject message)
    {
        if (!message.TryGetPropertyValue("TickDt", out var node) || node is not JsonValue v || !v.TryGetValue<string>(out var text)) return 0;
        var match = DatePattern().Match(text);
        return match.Success ? long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static JsonObject Normalize(JsonObject message, StreamMode mode)
    {
        if (mode == StreamMode.Ltp) return new JsonObject { ["ltp"] = Or(message, "LastRate"), ["ltt"] = Time(message) };
        if (mode == StreamMode.Quote)
        {
            return new JsonObject
            {
                ["ltp"] = Or(message, "LastRate"), ["ltt"] = Time(message), ["volume"] = Or(message, "TotalQty"), ["open"] = Or(message, "OpenRate"), ["high"] = Or(message, "High"), ["low"] = Or(message, "Low"),
                ["close"] = Or(message, "PClose"), ["last_trade_quantity"] = Or(message, "LastQty"), ["average_price"] = Or(message, "AvgRate"), ["total_buy_quantity"] = Or(message, "TBidQ"),
                ["total_sell_quantity"] = Or(message, "TOffQ"), ["bid_price"] = Or(message, "BidRate"), ["bid_quantity"] = Or(message, "BidQty"), ["ask_price"] = Or(message, "OffRate"), ["ask_quantity"] = Or(message, "OffQty"),
            };
        }

        var result = new JsonObject { ["ltp"] = Or(message, "LastRate"), ["total_buy_quantity"] = Or(message, "TBidQ"), ["total_sell_quantity"] = Or(message, "TOffQ") };
        if (message["Details"] is JsonArray details)
        {
            var buy = new JsonArray();
            var sell = new JsonArray();
            foreach (var d in details.OfType<JsonObject>())
            {
                var level = new JsonObject { ["price"] = Or(d, "Price"), ["quantity"] = Or(d, "Quantity"), ["orders"] = Or(d, "NumberOfOrders") };
                var flag = Held(d, "BbBuySellFlag", JsonValue.Create(0)) is JsonValue f && f.TryGetValue<double>(out var n) ? n : 0;
                if (flag == 66 && buy.Count < 5) buy.Add(level);
                else if (flag == 83 && sell.Count < 5) sell.Add(level);
            }

            result["depth"] = new JsonObject { ["buy"] = buy, ["sell"] = sell };
        }

        return result;
    }
}
