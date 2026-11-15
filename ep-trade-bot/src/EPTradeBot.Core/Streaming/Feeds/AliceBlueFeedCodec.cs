using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// AliceBlue's stream (broker/aliceblue/streaming): JSON over a WebSocket, signed in by a connect frame whose token is the user session hashed twice, answered by a ck frame (a cf frame says the same and changes nothing here). Subscriptions go in one
/// frame for each feed type (t for ticks, d for depth), scrips joined by a hash; an unsubscribe is one frame whatever the mode. One record is kept for a scrip: the highest mode asked for and every mode asked for. A tick is read into a snapshot that
/// keeps the last non-zero price, and is published to each mode the scrip was asked for: an acknowledgement as it was sent, a feed tick from the snapshot. When the answer says OK every subscription is asked for again, the scrips whose highest mode is
/// depth as depth and the rest as ticks. An index asks for its token without the 999 in front.
/// </summary>
public sealed class AliceBlueFeedCodec : IFeedCodec
{
    private static readonly string[] PriceFields = ["ltp", "open", "high", "low", "close", "average_price"];
    private static readonly string[] VolumeFields = ["volume", "total_buy_quantity", "total_sell_quantity"];
    private static readonly string[] OtherFields = ["total_oi", "change_percent", "timestamp", "symbol", "exchange", "token"];

    private sealed class Sub
    {
        public required string Symbol { get; set; }

        public required string Exchange { get; set; }

        public required string AbExchange { get; set; }

        public required string Token { get; set; }

        public int Mode { get; set; }

        public HashSet<int> Modes { get; } = [];
    }

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly List<(string AbExchange, string Token, string Feed)> queue = [];
    private readonly Dictionary<string, JsonObject> snapshots = [];
    private readonly List<string> outgoing = [];
    private string session = "";
    private string clientId = "";

    public string Broker => "aliceblue";

    private static string Mapped(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", "MCX_INDEX" => "MCX", _ => exchange };

    private static bool IsIndex(string exchange) => exchange is "NSE_INDEX" or "BSE_INDEX" or "MCX_INDEX";

    public void Begin(EPTradeBot.Core.Brokers.BrokerSession brokerSession)
    {
        lock (gate)
        {
            session = brokerSession.AuthToken;
            clientId = brokerSession.ClientId ?? "";
        }
    }

    private static string Frame(string feed, IEnumerable<string> keys) => new JsonObject { ["k"] = string.Join('#', keys), ["t"] = feed }.ToJsonString();

    /// <summary>The queued scrips, one frame for each feed type in the order the types first appear, each scrip once.</summary>
    private List<string> Flush()
    {
        var groups = new OrderedDictionary<string, List<string>>();
        foreach (var (ab, token, feed) in queue)
        {
            if (!groups.TryGetValue(feed, out var list)) groups[feed] = list = [];
            list.Add($"{ab}|{token}");
        }

        queue.Clear();
        return groups.Select(g => Frame(g.Key, g.Value.Distinct())).ToList();
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            foreach (var r in requests)
            {
                if (!double.TryParse(r.Token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) continue;
                var token = ((long)Math.Truncate(number)).ToString(CultureInfo.InvariantCulture);
                if (IsIndex(r.Exchange) && token.StartsWith("999", StringComparison.Ordinal)) token = token[3..];
                var ab = Mapped(r.Exchange);
                var mode = (int)r.Mode;
                var key = $"{ab}|{token}";
                if (!subs.TryGetValue(key, out var existing))
                {
                    var sub = new Sub { Symbol = r.Symbol, Exchange = r.Exchange, AbExchange = ab, Token = token, Mode = mode };
                    sub.Modes.Add(mode);
                    subs[key] = sub;
                }
                else if (mode > existing.Mode)
                {
                    existing.Symbol = r.Symbol;
                    existing.Exchange = r.Exchange;
                    existing.Mode = mode;
                    existing.Modes.Add(mode);
                }
                else
                {
                    existing.Modes.Add(mode);
                }

                queue.Add((ab, token, mode == 3 ? "d" : "t"));
            }

            return Flush();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var ab = Mapped(target.Exchange);
            var key = $"{ab}|{target.Token}";
            var frames = new List<string>();
            var modes = mode is { } one ? [(int)one] : subs.TryGetValue(key, out var held) ? held.Modes.OrderBy(m => m).ToList() : [2];
            foreach (var m in modes)
            {
                frames.Add(Frame("u", [key]));
                if (!subs.TryGetValue(key, out var sub)) continue;
                sub.Modes.Remove(m);
                if (sub.Modes.Count == 0)
                {
                    subs.Remove(key);
                    snapshots.Remove(key);
                }
                else
                {
                    sub.Mode = sub.Modes.Max();
                }
            }

            return frames;
        }
    }

    /// <summary>A new connection: signed in first; the subscriptions are asked for again when the answer says OK.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            queue.Clear();
            if (session.Length == 0) return [];
            var inner = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)));
            var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inner)));
            return [new JsonObject { ["susertoken"] = token, ["t"] = "c", ["actid"] = $"{clientId}_API", ["uid"] = $"{clientId}_API", ["source"] = "API" }.ToJsonString()];
        }
    }

    public IReadOnlyList<string> TakeOutgoing()
    {
        lock (gate)
        {
            var taken = outgoing.ToList();
            outgoing.Clear();
            return taken;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            queue.Clear();
            snapshots.Clear();
            outgoing.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    // ---- reading -----------------------------------------------------------------------------------------------

    private static string? Text(JsonObject message, string name) => message.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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

        if (parsed is not JsonObject data) return [];
        lock (gate)
        {
            try
            {
                switch (Text(data, "t"))
                {
                    case "ck":
                        if (Text(data, "s") == "OK") RepeatAll();
                        return [];
                    case "tk" or "tf":
                        return OnData(data, ParseTick(data), nowMs);
                    case "dk" or "df":
                        return OnData(data, ParseDepth(data), nowMs);
                    default:
                        return [];
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                // A number the feed cannot read stops the message where it is.
                return [];
            }
        }
    }

    private void RepeatAll()
    {
        var groups = new OrderedDictionary<string, List<string>>();
        foreach (var sub in subs.Values)
        {
            var feed = sub.Mode == 3 ? "d" : "t";
            if (!groups.TryGetValue(feed, out var list)) groups[feed] = list = [];
            list.Add($"{sub.AbExchange}|{sub.Token}");
        }

        foreach (var (feed, keys) in groups) outgoing.Add(Frame(feed, keys.Distinct()));
    }

    // ---- Python's number reading ---------------------------------------------------------------------------------

    private static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is JsonArray a) return a.Count > 0;
        if (node is JsonObject o) return o.Count > 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var text)) return text.Length > 0;
            if (v.TryGetValue<bool>(out var flag)) return flag;
            if (v.TryGetValue<double>(out var d)) return d != 0;
        }

        return true;
    }

    private static double PyFloat(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
            if (v.TryGetValue<string>(out var text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }

        throw new FormatException("Not a number.");
    }

    private static long PyInt(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d))
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("Not an integer.");
                return (long)Math.Truncate(d);
            }

            if (v.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
            if (v.TryGetValue<string>(out var text) && long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }

        throw new FormatException("Not an integer.");
    }

    private static JsonNode? Field(JsonObject data, string name) => data.TryGetPropertyValue(name, out var node) ? node : null;

    private static double GuardedFloat(JsonObject data, string name) => Truthy(Field(data, name)) ? PyFloat(Field(data, name)) : 0.0;

    private static long GuardedInt(JsonObject data, string name) => Truthy(Field(data, name)) ? PyInt(Field(data, name)) : 0;

    /// <summary>aliceblue_mapping.parse_tick_data: a tk carries everything (a missing or empty number is zero), a tf only what it names.</summary>
    private static JsonObject ParseTick(JsonObject data)
    {
        var parsed = new JsonObject { ["type"] = "tick_data", ["message_type"] = Text(data, "t"), ["exchange"] = Field(data, "e")?.DeepClone() ?? "", ["token"] = Field(data, "tk")?.DeepClone() ?? "" };
        if (Text(data, "t") == "tk")
        {
            var raw = Text(data, "ts");
            parsed["symbol"] = string.IsNullOrEmpty(raw) ? "" : raw.Split('-')[0];
            parsed["ltp"] = GuardedFloat(data, "lp");
            parsed["volume"] = GuardedInt(data, "v");
            parsed["open"] = GuardedFloat(data, "o");
            parsed["high"] = GuardedFloat(data, "h");
            parsed["low"] = GuardedFloat(data, "l");
            parsed["close"] = GuardedFloat(data, "c");
            parsed["change_percent"] = GuardedFloat(data, "pc");
            parsed["change_value"] = GuardedFloat(data, "cv");
            parsed["average_price"] = GuardedFloat(data, "ap");
            parsed["timestamp"] = data.ContainsKey("ft") ? Field(data, "ft")?.DeepClone() : "";
            parsed["total_oi"] = GuardedInt(data, "toi");
            parsed["tick_size"] = GuardedFloat(data, "ti");
            parsed["lot_size"] = GuardedInt(data, "ls");
            parsed["market_lot"] = GuardedInt(data, "ml");
            parsed["price_precision"] = GuardedInt(data, "pp");
        }
        else
        {
            if (data.ContainsKey("lp")) parsed["ltp"] = PyFloat(data["lp"]);
            if (data.ContainsKey("pc")) parsed["change_percent"] = PyFloat(data["pc"]);
            if (data.ContainsKey("ft")) parsed["timestamp"] = data["ft"]?.DeepClone();
            if (data.ContainsKey("v")) parsed["volume"] = PyInt(data["v"]);
            if (data.ContainsKey("toi")) parsed["total_oi"] = PyInt(data["toi"]);
            foreach (var (key, name) in new[] { ("o", "open"), ("h", "high"), ("l", "low"), ("c", "close"), ("cv", "change_value"), ("ap", "average_price") })
            {
                if (data.ContainsKey(key)) parsed[name] = PyFloat(data[key]);
            }
        }

        return parsed;
    }

    /// <summary>aliceblue_mapping.parse_depth_data: five levels a side, a level whose price or quantity cannot be read, or whose price is not above zero, left out.</summary>
    private static JsonObject ParseDepth(JsonObject data)
    {
        var bids = new JsonArray();
        var asks = new JsonArray();
        for (var i = 1; i <= 5; i++)
        {
            AddLevel(bids, data, $"bp{i}", $"bq{i}");
            AddLevel(asks, data, $"sp{i}", $"sq{i}");
        }

        return new JsonObject
        {
            ["type"] = "market_depth", ["message_type"] = Text(data, "t"), ["exchange"] = Field(data, "e")?.DeepClone(), ["token"] = Field(data, "tk")?.DeepClone(), ["bids"] = bids, ["asks"] = asks,
            ["timestamp"] = data.ContainsKey("ft") ? Field(data, "ft")?.DeepClone() : "", ["ltp"] = GuardedFloat(data, "lp"),
        };
    }

    private static void AddLevel(JsonArray side, JsonObject data, string priceName, string quantityName)
    {
        try
        {
            var price = PyFloat(data.ContainsKey(priceName) ? data[priceName] : JsonValue.Create("0"));
            var quantity = PyInt(data.ContainsKey(quantityName) ? data[quantityName] : JsonValue.Create("0"));
            if (price > 0) side.Add(new JsonObject { ["price"] = price, ["quantity"] = quantity });
        }
        catch (FormatException)
        {
        }
    }

    // ---- the snapshot and what is published ------------------------------------------------------------------------

    private static bool IsNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.TryGetValue<string>(out _) || v.TryGetValue<bool>(out _)) return false;
        if (v.TryGetValue<double>(out value)) return true;
        if (!v.TryGetValue<long>(out var whole)) return false;
        value = whole;
        return true;
    }

    private JsonObject Update(string key, JsonObject parsed)
    {
        if (!snapshots.TryGetValue(key, out var snapshot)) snapshots[key] = snapshot = [];
        foreach (var field in PriceFields)
        {
            if (!parsed.ContainsKey(field)) continue;
            if (IsNumber(parsed[field], out var d) && d != 0) snapshot[field] = parsed[field]!.DeepClone();
            else if (!snapshot.ContainsKey(field)) snapshot[field] = 0;
        }

        foreach (var field in VolumeFields)
        {
            if (!parsed.ContainsKey(field)) continue;
            if (IsNumber(parsed[field], out var d) && d >= 0) snapshot[field] = parsed[field]!.DeepClone();
            else if (!snapshot.ContainsKey(field)) snapshot[field] = 0;
        }

        foreach (var field in OtherFields)
        {
            if (parsed.ContainsKey(field) && parsed[field] is not null) snapshot[field] = parsed[field]!.DeepClone();
        }

        foreach (var side in new[] { "bids", "asks" })
        {
            if (!parsed.ContainsKey(side) || parsed[side] is not JsonArray levels) continue;
            var valid = new JsonArray(levels.OfType<JsonObject>().Where(l => !IsNumber(l["price"], out var p) || p != 0).Select(l => (JsonNode)l.DeepClone()).ToArray());
            if (valid.Count > 0) snapshot[side] = valid;
            else if (!snapshot.ContainsKey(side)) snapshot[side] = new JsonArray();
        }

        return snapshot;
    }

    private static JsonNode? Get(JsonObject data, string name, JsonNode? fallback) => data.ContainsKey(name) ? data[name]?.DeepClone() : fallback;

    private List<FeedPublish> OnData(JsonObject message, JsonObject parsed, long nowMs)
    {
        var abExchange = Text(message, "e") ?? "";
        var token = Text(message, "tk") ?? "";
        var key = $"{abExchange}|{token}";
        var type = Text(message, "t") ?? "";
        var snapshot = Update(key, parsed);
        var data = type is "tf" or "df" ? snapshot : parsed;
        if (!subs.TryGetValue(key, out var sub)) return [];
        // The scrip's own name replaces the one the broker sent, in what is kept as well as in what goes out.
        data["symbol"] = sub.Symbol;
        var modes = sub.Modes;
        var hasDepth = data.ContainsKey("bids") || data.ContainsKey("asks") || data.ContainsKey("depth");
        var hasQuote = new[] { "open", "high", "low", "close", "volume" }.Any(data.ContainsKey);
        var hasLtp = data.ContainsKey("ltp");
        var topics = new List<int>();
        if (type is "df" or "dk" && modes.Contains(3))
        {
            topics.Add(3);
        }
        else
        {
            if (hasLtp && modes.Contains(1)) topics.Add(1);
            if (hasQuote && modes.Contains(2)) topics.Add(2);
            if (hasDepth && modes.Contains(3)) topics.Add(3);
        }

        if (topics.Count == 0 && modes.Count > 0) topics.Add(modes.Max());
        var published = new List<FeedPublish>();
        foreach (var mode in topics)
        {
            var payload = mode switch
            {
                1 => new JsonObject { ["ltp"] = Get(data, "ltp", 0.0), ["ltt"] = Get(data, "timestamp", "") },
                2 => new JsonObject
                {
                    ["ltp"] = Get(data, "ltp", 0.0), ["ltt"] = Get(data, "timestamp", ""), ["volume"] = Get(data, "volume", 0), ["open"] = Get(data, "open", 0.0), ["high"] = Get(data, "high", 0.0), ["low"] = Get(data, "low", 0.0),
                    ["close"] = Get(data, "close", 0.0), ["change_percent"] = Get(data, "change_percent", 0.0), ["average_price"] = Get(data, "average_price", 0.0), ["total_oi"] = Get(data, "total_oi", 0),
                },
                _ => DepthPayload(data),
            };
            published.Add(new FeedPublish(MarketTopics.Format(sub.Exchange, sub.Symbol, (StreamMode)mode), payload));
        }

        return published;
    }

    private static JsonObject DepthPayload(JsonObject data)
    {
        if (Text(data, "type") == "market_depth" || data.ContainsKey("bids") || data.ContainsKey("asks"))
        {
            JsonArray Side(string name)
            {
                var side = new JsonArray();
                foreach (var level in (data.ContainsKey(name) ? data[name] as JsonArray : null)?.OfType<JsonObject>() ?? [])
                    side.Add(new JsonObject { ["price"] = Get(level, "price", 0), ["quantity"] = Get(level, "quantity", 0), ["orders"] = 0 });
                return side;
            }

            return new JsonObject { ["ltp"] = Get(data, "ltp", 0), ["timestamp"] = Get(data, "timestamp", ""), ["depth"] = new JsonObject { ["buy"] = Side("bids"), ["sell"] = Side("asks") } };
        }

        var fallback = new JsonObject();
        foreach (var (name, value) in data)
        {
            if (name is "message_type" or "type") continue;
            fallback[name] = value?.DeepClone();
        }

        return fallback;
    }
}
