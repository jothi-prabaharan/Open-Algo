using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Upstox's market-data socket v3 (broker/upstox/streaming/upstox_client.py and upstox_adapter.py). Subscriptions are JSON sent as binary frames, one
/// message per Upstox mode (LTP is <c>ltpc</c>; Quote and Depth are both <c>full</c> and share a message); what comes back is protobuf. Upstox caps the keys
/// per mode and in total, and the codec holds to those caps. A tick that carries no price is given the last one seen for the instrument.
/// </summary>
public sealed class UpstoxFeedCodec : IFeedCodec
{
    private static readonly Dictionary<string, int> KeyLimits = new() { ["ltpc"] = 5000, ["option_greeks"] = 3000, ["full"] = 2000, ["full_d30"] = 50 };
    private static readonly Dictionary<string, int> CombinedLimits = new() { ["ltpc"] = 2000, ["option_greeks"] = 2000, ["full"] = 1500, ["full_d30"] = 1500 };
    private const int DefaultKeyLimit = 2000;
    private const int DefaultCombinedLimit = 1500;

    private sealed record Sub(string Symbol, string Exchange, int Mode, string Token, string InstrumentKey);

    private readonly Lock gate = new();
    private readonly List<Sub> subs = [];
    private readonly Dictionary<string, HashSet<string>> keysByMode = [];
    private readonly Dictionary<string, JsonObject> lastPrice = [];

    public string Broker => "upstox";

    public bool SendsBinary => true;

    private static string ModeOf(int mode) => mode == 1 ? "ltpc" : "full";

    private static string Message(string method, IEnumerable<string> keys, string? mode)
    {
        var data = new JsonObject { ["instrumentKeys"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()) };
        if (mode is not null && method == "sub") data["mode"] = mode;
        return new JsonObject { ["guid"] = Guid.NewGuid().ToString("N")[..20], ["method"] = method, ["data"] = data }.ToJsonString();
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queued = new List<Sub>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3) continue;
                if (subs.Any(s => s.Symbol == r.Symbol && s.Exchange == r.Exchange && s.Mode == mode)) continue;
                var token = r.Token.Contains('|', StringComparison.Ordinal) ? r.Token[(r.Token.LastIndexOf('|') + 1)..] : r.Token;
                var sub = new Sub(r.Symbol, r.Exchange, mode, r.Token, $"{r.BrokerExchange}|{token}");
                subs.Add(sub);
                queued.Add(sub);
            }

            return Send(queued);
        }
    }

    /// <summary>One bulk message per Upstox mode, in the order the modes first appear.</summary>
    private List<string> Send(List<Sub> queued)
    {
        var byMode = new List<(string Mode, List<string> Keys)>();
        foreach (var s in queued)
        {
            var mode = ModeOf(s.Mode);
            var at = byMode.FindIndex(g => g.Mode == mode);
            if (at < 0) byMode.Add((mode, [s.InstrumentKey]));
            else byMode[at].Keys.Add(s.InstrumentKey);
        }

        var messages = new List<string>();
        foreach (var (mode, keys) in byMode) messages.AddRange(SubscribeKeys(keys, mode));
        return messages;
    }

    private List<string> SubscribeKeys(List<string> instrumentKeys, string mode)
    {
        var already = keysByMode.TryGetValue(mode, out var held) ? held : [];
        var seen = new HashSet<string>();
        var fresh = new List<string>();
        foreach (var key in instrumentKeys)
        {
            if (already.Contains(key) || !seen.Add(key)) continue;
            fresh.Add(key);
        }

        if (fresh.Count == 0) return [];

        var limit = KeyLimits.GetValueOrDefault(mode, DefaultKeyLimit);
        var room = Math.Max(limit - already.Count, 0);
        if (fresh.Count > room) fresh = fresh.Take(room).ToList();
        if (fresh.Count == 0) return [];

        // A mode that still has an entry counts as in use even when every key was taken off it.
        var modesInUse = new HashSet<string>(keysByMode.Keys) { mode };
        if (modesInUse.Count > 1)
        {
            var budget = modesInUse.Min(m => CombinedLimits.GetValueOrDefault(m, DefaultCombinedLimit));
            var total = keysByMode.Values.Sum(v => v.Count);
            var left = Math.Max(budget - total, 0);
            if (fresh.Count > left) fresh = fresh.Take(left).ToList();
        }

        var messages = new List<string>();
        for (var start = 0; start < fresh.Count; start += limit)
        {
            var chunk = fresh.Skip(start).Take(limit).ToList();
            messages.Add(Message("sub", chunk, mode));
            if (!keysByMode.TryGetValue(mode, out var set)) keysByMode[mode] = set = [];
            set.UnionWith(chunk);
        }

        return messages;
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var messages = new List<string>();
            var found = subs.Where(s => s.Symbol == target.Symbol && s.Exchange == target.Exchange && (mode is null || s.Mode == (int)mode)).ToList();
            foreach (var s in found)
            {
                messages.Add(Message("unsub", [s.InstrumentKey], null));
                // The cap budget is freed too, so a session that churns instruments is not refused well below the real limit.
                foreach (var set in keysByMode.Values) set.Remove(s.InstrumentKey);
                subs.Remove(s);
            }

            return messages;
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            keysByMode.Clear();
            return Send([.. subs]);
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            keysByMode.Clear();
            lastPrice.Clear();
        }
    }

    // ---- reading what comes back -----------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        JsonObject data;
        try
        {
            data = UpstoxProtobuf.DecodeFeedResponse(frame);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException)
        {
            return [];
        }

        if (data["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "market_info") return [];
        if (data["feeds"] is not JsonObject feeds || feeds.Count == 0) return [];

        // currentTs is a 64-bit number, which the reader gives as text; it travels as it came, as OpenAlgo's does.
        JsonNode currentTs = data["currentTs"]?.DeepClone() ?? JsonValue.Create(0);
        var published = new List<FeedPublish>();
        lock (gate)
        {
            foreach (var (feedKey, feedNode) in feeds)
            {
                if (feedNode is not JsonObject feed) continue;
                foreach (var sub in subs.Where(s => Matches(s, feedKey)).ToList())
                {
                    var marketData = Extract(feed, sub, currentTs);
                    if (marketData.Count == 0) continue;
                    if (sub.Mode == 3)
                    {
                        var depth = (JsonObject)marketData.DeepClone();
                        var levels = new JsonObject
                        {
                            ["buy"] = Take(depth, "buy") ?? new JsonArray(), ["sell"] = Take(depth, "sell") ?? new JsonArray(),
                            ["timestamp"] = depth["timestamp"]!.DeepClone(),
                        };
                        depth["depth"] = levels;
                        published.Add(new FeedPublish(Topic(sub), depth));
                    }
                    else
                    {
                        published.Add(new FeedPublish(Topic(sub), marketData));
                    }
                }
            }
        }

        return published;
    }

    private static JsonNode? Take(JsonObject o, string name)
    {
        var node = o[name];
        o.Remove(name);
        return node;
    }

    private static string Topic(Sub s) => $"{s.Exchange}_{s.Symbol}_{(s.Mode == 1 ? "LTP" : s.Mode == 3 ? "DEPTH" : "QUOTE")}";

    private static bool Matches(Sub s, string feedKey)
    {
        if (s.InstrumentKey == feedKey) return true;
        if (!feedKey.Contains('|', StringComparison.Ordinal)) return false;
        var token = feedKey[(feedKey.LastIndexOf('|') + 1)..];
        return s.Token == token || s.Token == feedKey;
    }

    // ---- numbers as OpenAlgo reads them ----------------------------------------------------------------------

    private static double Float(JsonNode? node, double fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    private static long Whole(JsonNode? node) => (long)Math.Truncate(Float(node));

    private static JsonObject Obj(JsonNode? node) => node as JsonObject ?? [];

    // ---- the three modes -------------------------------------------------------------------------------------

    private JsonObject Extract(JsonObject feed, Sub sub, JsonNode currentTs)
    {
        var instrumentKey = sub.InstrumentKey;
        var baseData = new JsonObject { ["symbol"] = sub.Symbol, ["exchange"] = sub.Exchange, ["token"] = sub.Token };

        // The last price on any tick, whichever branch it came under, is kept for ticks that carry none.
        var tickLtpc = TickLtpc(feed);
        if (tickLtpc.Count > 0) lastPrice[instrumentKey] = (JsonObject)tickLtpc.DeepClone();

        JsonObject result;
        switch (sub.Mode)
        {
            case 1: result = LtpData(feed, baseData); break;
            case 2: result = QuoteData(feed, baseData, currentTs); break;
            case 3:
                result = DepthData(feed, currentTs);
                foreach (var (name, value) in baseData) result[name] = value?.DeepClone();
                break;
            default: return [];
        }

        // An indicative price is only valid for the tick that carried it, so only the trade fields are carried forward.
        if (result.Count == 0)
        {
            if (lastPrice.TryGetValue(instrumentKey, out var cached) && cached.Count > 0)
            {
                result = (JsonObject)baseData.DeepClone();
                result["ltp"] = Float(cached["ltp"]);
                result["ltq"] = Whole(cached["ltq"]);
                result["ltt"] = Whole(cached["ltt"]);
                result["cp"] = Float(cached["cp"]);
                result["timestamp"] = currentTs.DeepClone();
            }
        }
        else if (Float(result["ltp"]) == 0)
        {
            if (lastPrice.TryGetValue(instrumentKey, out var cached) && Float(cached["ltp"]) != 0) result["ltp"] = Float(cached["ltp"]);
        }

        return result;
    }

    private static JsonObject TickLtpc(JsonObject feed)
    {
        if (feed["ltpc"] is JsonObject direct) return direct;
        var fullFeed = Obj(feed["fullFeed"]);
        var ff = fullFeed["marketFF"] as JsonObject ?? fullFeed["indexFF"] as JsonObject ?? [];
        return Obj(ff["ltpc"]);
    }

    private static JsonObject LtpData(JsonObject feed, JsonObject baseData)
    {
        var data = (JsonObject)baseData.DeepClone();
        if (feed["ltpc"] is not JsonObject ltpc) return data;
        data["ltp"] = Float(ltpc["ltp"]);
        data["ltq"] = Whole(ltpc["ltq"]);
        data["ltt"] = Whole(ltpc["ltt"]);
        data["cp"] = Float(ltpc["cp"]);
        // Only the wrapped indicative price rides on a bare last-price tick.
        if (ltpc["iep"] is not null) data["indicative_equilibrium_price"] = Float(ltpc["iep"]);
        return data;
    }

    private static JsonObject QuoteData(JsonObject feed, JsonObject baseData, JsonNode currentTs)
    {
        if (feed["fullFeed"] is not JsonObject fullFeed) return [];
        var ff = fullFeed["marketFF"] as JsonObject is { Count: > 0 } market ? market : fullFeed["indexFF"] as JsonObject ?? [];
        var ltpc = Obj(ff["ltpc"]);
        var candles = (Obj(ff["marketOHLC"])["ohlc"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var candle = candles.FirstOrDefault(c => c["interval"] is JsonValue i && i.TryGetValue<string>(out var s) && s == "1d") ?? candles.FirstOrDefault() ?? [];

        var data = (JsonObject)baseData.DeepClone();
        data["open"] = Float(candle["open"]);
        data["high"] = Float(candle["high"]);
        data["low"] = Float(candle["low"]);
        data["close"] = Float(candle["close"]);
        data["ltp"] = Float(ltpc["ltp"]);
        data["last_trade_quantity"] = Whole(ltpc["ltq"]);
        data["volume"] = Whole(candle["vol"]);
        data["average_price"] = Float(ff["atp"]);
        data["total_buy_quantity"] = Whole(ff["tbq"]);
        data["total_sell_quantity"] = Whole(ff["tsq"]);
        data["timestamp"] = candle["ts"] is not null ? Whole(candle["ts"]) : Whole(currentTs);
        foreach (var (name, value) in CasFields(ff)) data[name] = value;
        return data;
    }

    private static JsonObject DepthData(JsonObject feed, JsonNode currentTs)
    {
        if (feed["fullFeed"] is not JsonObject fullFeed)
            return new JsonObject { ["buy"] = new JsonArray(), ["sell"] = new JsonArray(), ["timestamp"] = currentTs.DeepClone(), ["ltp"] = 0 };

        var ff = fullFeed["marketFF"] as JsonObject is { Count: > 0 } market ? market : fullFeed["indexFF"] as JsonObject ?? [];
        var quotes = (Obj(ff["marketLevel"])["bidAskQuote"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var buys = new List<(double Price, long Quantity)>();
        var sells = new List<(double Price, long Quantity)>();
        foreach (var level in quotes)
        {
            var bid = Float(level["bidP"]);
            if (bid > 0) buys.Add((bid, Whole(level["bidQ"])));
            var ask = Float(level["askP"]);
            if (ask > 0) sells.Add((ask, Whole(level["askQ"])));
        }

        static JsonArray Five(IEnumerable<(double Price, long Quantity)> levels)
        {
            var list = levels.Select(l => new JsonObject { ["price"] = l.Price, ["quantity"] = l.Quantity, ["orders"] = 0 }).ToList();
            while (list.Count < 5) list.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 });
            return new JsonArray(list.Take(5).Select(l => (JsonNode?)l).ToArray());
        }

        var data = new JsonObject
        {
            // Best price first on each side; a stable sort keeps equal prices in the order they came.
            ["buy"] = Five(buys.OrderByDescending(l => l.Price)),
            ["sell"] = Five(sells.OrderBy(l => l.Price)),
            ["timestamp"] = currentTs.DeepClone(),
            ["ltp"] = Float(Obj(ff["ltpc"])["ltp"]),
        };
        foreach (var (name, value) in CasFields(ff)) data[name] = value;
        return data;
    }

    /// <summary>The auction and pre-open extras, present only while an auction runs; a field Upstox leaves out stays out rather than becoming zero.</summary>
    private static List<(string Name, JsonNode Value)> CasFields(JsonObject ff)
    {
        var fields = new List<(string, JsonNode)>();
        var iep = ff["iep"] is { } plain ? (double?)Float(plain) : Obj(ff["ltpc"])["iep"] is { } wrapped ? Float(wrapped) : null;
        if (iep is { } price) fields.Add(("indicative_equilibrium_price", JsonValue.Create(price)));
        if (ff["rp"] is { } rp) fields.Add(("reference_price", JsonValue.Create(Float(rp))));
        if (ff["ieq"] is { } ieq) fields.Add(("indicative_equilibrium_quantity", JsonValue.Create(Whole(ieq))));
        // A signed imbalance: negative means more sell quantity is left unmatched.
        if (ff["iiqTotal"] is { } total) fields.Add(("indicative_imbalance_quantity_total", JsonValue.Create(Whole(total))));
        if (ff["iiqM"] is { } market) fields.Add(("indicative_imbalance_quantity_market", JsonValue.Create(Whole(market))));
        if (ff["casEligible"] is { } eligible) fields.Add(("cas_eligible", JsonValue.Create(eligible is JsonValue v && v.TryGetValue<bool>(out var b) && b)));
        return fields;
    }
}
