using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// What Groww's socket token call gave for a session, handed from the plugin (which makes the call as the feed starts) to the codec of the connection that follows: the socket token (a JWT the NATS server checks) and the seed of the key it was made for.
/// Taken once, so a token is never used for two connections.
/// </summary>
public static class GrowwSocketCredentials
{
    private static readonly ConcurrentDictionary<string, (string Jwt, string Seed)> Held = new();

    public static void Put(string authToken, string jwt, string seed) => Held[authToken] = (jwt, seed);

    public static (string Jwt, string Seed)? Take(string authToken) => Held.TryRemove(authToken, out var value) ? value : null;
}

/// <summary>
/// Groww's market data stream (broker/groww/streaming): NATS over a WebSocket. The server's INFO carries a nonce; the client answers CONNECT with the socket token, its public key and the signed nonce, and a PING. Once the server has answered (a PONG), every
/// subscription is a SUB for /ld/&lt;eq|fo|comm|cds&gt;/&lt;exchange&gt;/price.&lt;token&gt; (LTP and Quote) or .../book.&lt;token&gt; (Depth), a batch followed by one PING; a depth subscription also takes a price subscription of its own, so that its ticks carry the
/// price. A message is protobuf. A price tick is shaped as a quote and given to every subscriber of the symbol (an LTP subscriber too), a book tick to depth subscribers only, and a depth subscriber's tick is the merge of what both topics have given. An
/// NSE index is subscribed by its symbol, a BSE one by its token. Two modes of one symbol share one topic, so unsubscribing one of them ends the topic for both, as OpenAlgo's adapter does. Subscriptions made before the server has accepted the connection are
/// sent when it does.
/// </summary>
public sealed class GrowwFeedCodec : IFeedCodec
{
    private sealed record Adapted(string Symbol, string Exchange, string GrowwExchange, string Segment, string Token, int Mode, bool Shadow);

    private sealed record Wire(string Symbol, string Exchange, string GrowwExchange, string Segment, string Token, string Mode, int NumericMode);

    private sealed record Spec(string CorrelationId, string Type, string GrowwExchange, string Segment, string Token, string Symbol, string Exchange, string? KeyOverride);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Adapted> adapted = [];
    private readonly Dictionary<string, string> keys = [];
    private readonly Dictionary<string, string> shadows = [];
    private readonly OrderedDictionary<string, Wire> wire = [];
    private readonly Dictionary<string, string> sids = [];
    private readonly Dictionary<(string, string, string), JsonObject> cache = [];
    private readonly List<string> outgoing = [];
    private string authToken = "";
    private string? jwt;
    private string? seed;
    private string? nonce;
    private bool connectSent;
    private bool authenticated;
    private int nextSid = 1;

    public string Broker => "groww";

    public void Begin(BrokerSession session)
    {
        lock (gate)
        {
            authToken = session.AuthToken;
            if (GrowwSocketCredentials.Take(session.AuthToken) is { } held) (jwt, seed) = held;
        }
    }

    // ---- subscribing -------------------------------------------------------------------------------------------

    private static (string Exchange, string Segment) Market(string exchange) => exchange switch
    {
        "NSE" or "NSE_INDEX" => ("NSE", "CASH"), "BSE" or "BSE_INDEX" => ("BSE", "CASH"), "NFO" => ("NSE", "FNO"), "BFO" => ("BSE", "FNO"), "MCX" => ("MCX", "COMM"), "CDS" => ("NSE", "CDS"), "BCD" => ("BSE", "CDS"),
        _ => (exchange, "CASH"),
    };

    private static string Topic(string exchange, string segment, string token, string mode)
    {
        var prefix = segment.ToUpperInvariant() switch { "CASH" => "eq", "FNO" => "fo", "COMM" => "comm", "CDS" => "cds", var other => other.ToLowerInvariant() };
        return $"/ld/{prefix}/{exchange.ToLowerInvariant()}/{(mode == "depth" ? "book" : "price")}.{token}";
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queue = new List<Spec>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3 || string.IsNullOrEmpty(r.Token)) continue;
                var token = r.Exchange == "NSE_INDEX" ? r.Symbol : r.Token;
                var (growwExchange, segment) = Market(r.Exchange);
                var cid = $"{r.Symbol}_{r.Exchange}_{mode}";
                var isIndex = r.Exchange.Contains("INDEX", StringComparison.OrdinalIgnoreCase);
                var type = "ltp";
                if (mode == 3)
                {
                    if (isIndex) mode = 1;
                    else type = "depth";
                }

                adapted[cid] = new Adapted(r.Symbol, r.Exchange, growwExchange, segment, token, mode, false);
                queue.Add(new Spec(cid, type, growwExchange, segment, token, r.Symbol, r.Exchange, null));
                if (type == "depth")
                {
                    var shadow = $"_shadow_ltp_{cid}";
                    shadows[cid] = shadow;
                    adapted[shadow] = new Adapted(r.Symbol, r.Exchange, growwExchange, segment, token, 1, true);
                    queue.Add(new Spec(shadow, "ltp", growwExchange, segment, token, r.Symbol, r.Exchange, shadow));
                }
            }

            if (queue.Count == 0) return [];
            var created = new List<string>();
            foreach (var spec in queue)
            {
                var key = spec.KeyOverride ?? $"{(spec.Type == "depth" ? "depth" : "ltp")}_{spec.GrowwExchange}_{spec.Segment}_{spec.Token}";
                wire[key] = new Wire(spec.Symbol, spec.Exchange, spec.GrowwExchange, spec.Segment, spec.Token, spec.Type, spec.Type == "depth" ? 3 : 1);
                keys[spec.CorrelationId] = key;
                created.Add(key);
            }

            return authenticated ? SubscribeFrames(created) : [];
        }
    }

    private List<string> SubscribeFrames(IEnumerable<string> subKeys)
    {
        var frames = new List<string>();
        foreach (var key in subKeys)
        {
            if (!wire.TryGetValue(key, out var w)) continue;
            var sid = (nextSid++).ToString(CultureInfo.InvariantCulture);
            frames.Add($"SUB {Topic(w.GrowwExchange, w.Segment, w.Token, w.Mode)} {sid}\r\n");
            sids[key] = sid;
        }

        if (frames.Count > 0) frames.Add("PING\r\n");
        return frames;
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            foreach (var m in mode is { } given ? [(int)given] : new[] { 1, 2, 3 })
            {
                var cid = $"{target.Symbol}_{target.Exchange}_{m}";
                if (!adapted.Remove(cid, out var removed)) continue;
                if (shadows.Remove(cid, out var shadow)) adapted.Remove(shadow);
                keys.Remove(cid, out var key);
                string? shadowKey = null;
                if (shadow is not null) keys.Remove(shadow, out shadowKey);
                if (key is not null) Unwire(key, frames);
                if (shadowKey is not null) Unwire(shadowKey, frames);
                if (!adapted.Values.Any(a => a.GrowwExchange == removed.GrowwExchange && a.Segment == removed.Segment && a.Token == removed.Token)) cache.Remove((removed.GrowwExchange, removed.Segment, removed.Token));
            }

            return authenticated ? frames : [];
        }
    }

    private void Unwire(string key, List<string> frames)
    {
        if (!wire.ContainsKey(key)) return;
        if (sids.Remove(key, out var sid)) frames.Add($"UNSUB {sid}\r\n");
        wire.Remove(key);
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            // A new connection: nothing is signed in, and the ids of the last one are gone with it. What it held goes out when the server has accepted the connection.
            nextSid = 1;
            nonce = null;
            connectSent = false;
            authenticated = false;
            sids.Clear();
            return [];
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            adapted.Clear();
            keys.Clear();
            shadows.Clear();
            wire.Clear();
            sids.Clear();
            cache.Clear();
            outgoing.Clear();
            nonce = null;
            connectSent = false;
            authenticated = false;
            nextSid = 1;
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

    // ---- frames ------------------------------------------------------------------------------------------------

    private string Connect()
    {
        var parts = new List<string>
        {
            "\"verbose\": false", "\"pedantic\": false", "\"tls_required\": true", $"\"jwt\": {JsonSerializer.Serialize(jwt ?? "")}", "\"protocol\": 1", "\"version\": \"2.10.18\"", "\"lang\": \"python3\"", "\"name\": \"nats.py\"", "\"headers\": true",
            "\"no_responders\": true",
        };
        if (!string.IsNullOrEmpty(seed) && !string.IsNullOrEmpty(nonce))
        {
            var raw = GrowwNkeys.DecodeSeed(seed);
            parts.Add($"\"nkey\": \"{GrowwNkeys.EncodePublicKey(raw)}\"");
            parts.Add($"\"sig\": \"{Convert.ToBase64String(GrowwNkeys.Sign(raw, Encoding.UTF8.GetBytes(nonce)))}\"");
        }

        return $"CONNECT {{{string.Join(", ", parts)}}}\r\n";
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var data = frame.ToArray();
        var text = Encoding.UTF8.GetString(data);
        var published = new List<FeedPublish>();
        lock (gate)
        {
            if (text.StartsWith("INFO", StringComparison.Ordinal))
            {
                OnInfo(text);
            }
            else if (text.IndexOf("MSG", StringComparison.Ordinal) is >= 0 and < 50)
            {
                OnMessage(data, text, nowMs, published);
            }
            else
            {
                foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("PING", StringComparison.Ordinal)) outgoing.Add("PONG\r\n");
                    else if (line.StartsWith("PONG", StringComparison.Ordinal)) Accepted();
                    else if (line.StartsWith("+OK", StringComparison.Ordinal)) Accepted(true);
                    else if (line.StartsWith("-ERR", StringComparison.Ordinal) && line[4..].Trim().Trim('\'', '"') is var error && (error.Contains("authorization", StringComparison.OrdinalIgnoreCase) || error.Contains("authentication", StringComparison.OrdinalIgnoreCase))) authenticated = false;
                }
            }
        }

        return published;
    }

    /// <summary>The server has accepted the connection: every subscription goes out, new ids for each, then one PING.</summary>
    private void Accepted(bool always = false)
    {
        if (authenticated || (!connectSent && !always)) return;
        authenticated = true;
        sids.Clear();
        outgoing.AddRange(SubscribeFrames(wire.Keys.ToList()));
    }

    private void OnInfo(string text)
    {
        var start = text.IndexOf('{');
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (start >= 0)
        {
            try
            {
                var json = JsonNode.Parse(end > start ? text[start..end] : text[start..]);
                if (json is JsonObject info && info["nonce"] is JsonValue n && n.TryGetValue<string>(out var value)) nonce = value;
            }
            catch (JsonException)
            {
            }
        }

        if (connectSent) return;
        connectSent = true;
        outgoing.Add(Connect());
        outgoing.Add("PING\r\n");
    }

    private void OnMessage(byte[] data, string text, long nowMs, List<FeedPublish> published)
    {
        var at = text.IndexOf("MSG", StringComparison.Ordinal);
        var headerEnd = text.IndexOf("\r\n", at, StringComparison.Ordinal);
        if (headerEnd < 0) return;
        var parts = text[at..headerEnd].Split(' ');
        if (parts.Length < 4 || !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) return;
        var headerStart = Array.IndexOf(data, (byte)'M');
        for (var i = 0; i + 2 < data.Length; i++)
            if (data[i] == 'M' && data[i + 1] == 'S' && data[i + 2] == 'G')
            {
                headerStart = i;
                break;
            }

        var end = FindCrLf(data, headerStart);
        if (end < 0) return;
        var payloadStart = end + 2;
        if (payloadStart + size > data.Length) return;
        Dispatch(parts[1], parts[2], data.AsSpan(payloadStart, size), nowMs, published);
    }

    private static int FindCrLf(byte[] data, int from)
    {
        for (var i = Math.Max(from, 0); i + 1 < data.Length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n') return i;
        return -1;
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => DecodeBinary(Encoding.UTF8.GetBytes(frame), nowMs);

    // ---- a market data message ------------------------------------------------------------------------------------

    private void Dispatch(string subject, string sid, ReadOnlySpan<byte> payload, long nowMs, List<FeedPublish> published)
    {
        var tick = GrowwProtobuf.Parse(payload);
        string? found = null;
        foreach (var (key, _) in wire)
            if (sids.TryGetValue(key, out var held) && held == sid)
            {
                found = key;
                break;
            }

        if (found is null && subject.Contains('.', StringComparison.Ordinal))
        {
            var token = subject[(subject.LastIndexOf('.') + 1)..];
            var kind = subject.Contains("price", StringComparison.Ordinal) ? "ltp" : subject.Contains("book", StringComparison.Ordinal) ? "depth" : null;
            foreach (var (key, w) in wire)
                if (w.Token == token && ((kind == "ltp" && w.Mode == "ltp") || (kind == "depth" && w.Mode == "depth")))
                {
                    found = key;
                    sids[key] = sid;
                    break;
                }
        }

        if (found is null) return;
        var wired = wire[found];
        OnData(tick, wired.Symbol, wired.Exchange, wired.Mode, nowMs, published);
    }

    private static double Number(JsonNode? node) => node is JsonValue v ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : 0 : 0;

    private static double Value(JsonObject o, string key) => o.TryGetPropertyValue(key, out var v) ? Number(v) : 0;

    private static bool Has(JsonObject o, string key) => Value(o, key) != 0;

    private void OnData(GrowwTick tick, string symbol, string exchange, string mode, long nowMs, List<FeedPublish> published)
    {
        var matches = adapted.Where(a => !a.Value.Shadow && a.Value.Symbol == symbol && a.Value.Exchange == exchange && ((mode == "ltp" && a.Value.Mode is 1 or 2 or 3) || (mode == "depth" && a.Value.Mode == 3))).ToList();
        if (matches.Count == 0) return;
        var kind = tick.Ltp is not null ? 1 : tick.Depth is not null ? 3 : 1;
        var normalized = Normalize(tick, nowMs);
        var sample = matches[0].Value;
        var cacheKey = (sample.GrowwExchange, sample.Segment, sample.Token);
        if (!cache.TryGetValue(cacheKey, out var entry)) cache[cacheKey] = entry = [];
        if (kind == 3)
        {
            if (normalized.ContainsKey("depth")) entry["depth"] = normalized["depth"]!.DeepClone();
            if (normalized.ContainsKey("ltt")) entry["ltt"] = normalized["ltt"]!.DeepClone();
        }
        else
        {
            foreach (var field in new[] { "ltp", "open", "high", "low", "close", "volume", "ltt" })
                if (normalized[field] is { } v) entry[field] = v.DeepClone();
        }

        var merged = (JsonObject)entry.DeepClone();
        foreach (var (_, sub) in matches)
        {
            JsonObject data;
            int actual;
            if (sub.Mode == 3)
            {
                if (merged.Count == 0) continue;
                data = (JsonObject)merged.DeepClone();
                actual = 3;
            }
            else
            {
                if (kind == 3) continue;
                data = (JsonObject)normalized.DeepClone();
                actual = sub.Mode;
            }

            var modeName = actual switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" };
            var topic = $"{sub.Exchange}_{sub.Symbol}_{modeName}";
            data["symbol"] = sub.Symbol;
            data["exchange"] = sub.Exchange;
            data["mode"] = actual;
            data["timestamp"] = nowMs;
            data["broker"] = "groww";
            data["topic"] = topic;
            data["subscription_mode"] = sub.Mode;
            if (actual == 1)
            {
                if (!data.ContainsKey("ltt")) data["ltt"] = nowMs;
            }
            else if (actual == 2)
            {
                foreach (var field in new[] { "open", "high", "low", "close", "volume", "ltp" })
                    if (!data.ContainsKey(field)) data[field] = field == "volume" ? 0 : 0.0;
            }
            else
            {
                if (!data.ContainsKey("depth")) data["depth"] = new JsonObject { ["buy"] = new JsonArray(), ["sell"] = new JsonArray() };
                var depth = (JsonObject)data["depth"]!;
                if (depth["buy"] is JsonArray { Count: > 0 } buy && buy[0] is JsonObject top)
                {
                    if (Truthy(top["price"])) { data["bid"] = top["price"]!.DeepClone(); data["bid_price"] = top["price"]!.DeepClone(); }
                    if (Truthy(top["quantity"]))
                        foreach (var name in new[] { "bid_qty", "bid_size", "bid_quantity" }) data[name] = top["quantity"]!.DeepClone();
                }

                if (depth["sell"] is JsonArray { Count: > 0 } sell && sell[0] is JsonObject low)
                {
                    if (Truthy(low["price"]))
                        foreach (var name in new[] { "ask", "ask_price", "offer_price" }) data[name] = low["price"]!.DeepClone();
                    if (Truthy(low["quantity"]))
                        foreach (var name in new[] { "ask_qty", "ask_size", "ask_quantity", "offer_quantity" }) data[name] = low["quantity"]!.DeepClone();
                }
            }

            published.Add(new FeedPublish(topic, data));
        }
    }

    private static bool Truthy(JsonNode? node) => Number(node) != 0;

    private static JsonObject Normalize(GrowwTick tick, long nowMs)
    {
        if (tick.Ltp is { } ltp)
        {
            var quote = new JsonObject { ["ltp"] = Get(ltp, "ltp", 0), ["ltt"] = ltp.ContainsKey("timestamp") ? Get(ltp, "timestamp", 0) : nowMs };
            foreach (var field in new[] { "open", "high", "low", "close", "volume", "value" })
                if (Has(ltp, field)) quote[field] = ltp[field]!.DeepClone();
            return quote;
        }

        if (tick.Depth is { } depth)
        {
            static JsonArray Real(JsonArray levels) => new(levels.OfType<JsonObject>().Take(5).Where(l => Value(l, "price") > 0 || Value(l, "quantity") > 0).Select(l => (JsonNode?)l.DeepClone()).ToArray());
            return new JsonObject { ["ltt"] = depth.ContainsKey("timestamp") ? Get(depth, "timestamp", 0) : nowMs, ["depth"] = new JsonObject { ["buy"] = Real((JsonArray)depth["buy"]!), ["sell"] = Real((JsonArray)depth["sell"]!) } };
        }

        if (tick.Index is { } index) return new JsonObject { ["ltp"] = Get(index, "value", 0), ["ltt"] = index.ContainsKey("timestamp") ? Get(index, "timestamp", 0) : nowMs };
        return new JsonObject { ["ltp"] = 0, ["ltt"] = nowMs, ["volume"] = 0, ["open"] = 0, ["high"] = 0, ["low"] = 0, ["close"] = 0 };
    }

    private static JsonNode Get(JsonObject o, string key, double fallback) => o.TryGetPropertyValue(key, out var v) && v is not null ? v.DeepClone() : JsonValue.Create(fallback);
}

/// <summary>What a Groww market data message holds: a price, a book or an index value, each as the fields the protobuf gave.</summary>
public sealed record GrowwTick(JsonObject? Ltp, JsonObject? Depth, JsonObject? Index);

/// <summary>Groww's protobuf (broker/groww/streaming/groww_protobuf.py), read without a generated class: the field numbers decide, and a number is read as eight bytes whatever wire type it was sent as, as OpenAlgo's reader does.</summary>
public static class GrowwProtobuf
{
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        public int Position;

        public readonly bool Done => Position >= data.Length;

        public readonly int Length => data.Length;

        public ulong Varint()
        {
            ulong result = 0;
            var shift = 0;
            while (Position < data.Length)
            {
                var b = data[Position++];
                if (shift < 64) result |= (ulong)(b & 0x7f) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }

            return result;
        }

        public (int Field, int Wire) Tag()
        {
            if (Position >= data.Length) return (0, 0);
            var tag = Varint();
            return ((int)(tag >> 3), (int)(tag & 7));
        }

        public double Fixed64()
        {
            if (Position + 8 > data.Length) return 0.0;
            var value = BitConverter.ToDouble(data.Slice(Position, 8));
            Position += 8;
            return value;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Position += 8; break;
                case 2: Position += (int)Math.Min(Varint(), int.MaxValue / 2); break;
                case 5: Position += 4; break;
            }
        }
    }

    public static GrowwTick Parse(ReadOnlySpan<byte> payload)
    {
        JsonObject? ltp = null, depth = null, index = null;
        var r = new Reader(payload);
        while (!r.Done)
        {
            var (field, wire) = r.Tag();
            if (field == 0) break;
            switch (field)
            {
                case 1:
                    r.Skip(2 == wire ? 2 : wire);
                    break;
                case 2 or 3:
                    r.Varint();
                    break;
                case 4: ltp = Price(ref r); break;
                case 5: depth = Depth(ref r); break;
                case 6: index = Index(ref r); break;
                default: r.Skip(wire); break;
            }
        }

        return new GrowwTick(ltp, depth, index);
    }

    private static JsonObject Price(ref Reader r)
    {
        var length = (int)Math.Min(r.Varint(), int.MaxValue / 2);
        var end = r.Position + length;
        var result = new JsonObject();
        string[] names = ["", "timestamp", "open", "high", "low", "close", "volume", "value"];
        while (r.Position < end)
        {
            var (field, wire) = r.Tag();
            if (field == 0) break;
            if (field is >= 1 and <= 7) result[names[field]] = r.Fixed64();
            else if (field == 13) result["ltp"] = r.Fixed64();
            else r.Skip(wire);
        }

        r.Position = end;
        return result;
    }

    private static JsonObject Depth(ref Reader r)
    {
        var length = (int)Math.Min(r.Varint(), int.MaxValue / 2);
        var end = r.Position + length;
        var buy = new JsonArray();
        var sell = new JsonArray();
        var result = new JsonObject { ["timestamp"] = 0, ["buy"] = buy, ["sell"] = sell };
        while (r.Position < end)
        {
            var (field, wire) = r.Tag();
            if (field == 0) break;
            if (field == 1) result["timestamp"] = r.Fixed64();
            else if (field == 2) buy.Add(Level(ref r));
            else if (field == 3) sell.Add(Level(ref r));
            else r.Skip(wire);
        }

        r.Position = end;
        return result;
    }

    private static JsonObject Level(ref Reader r)
    {
        var length = (int)Math.Min(r.Varint(), int.MaxValue / 2);
        var end = r.Position + length;
        var level = new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 };
        while (r.Position < end)
        {
            var (field, wire) = r.Tag();
            if (field == 0) break;
            if (field == 1)
            {
                level["orders"] = (long)r.Varint();
            }
            else if (field == 2)
            {
                var subEnd = r.Position + (int)Math.Min(r.Varint(), int.MaxValue / 2);
                while (r.Position < subEnd)
                {
                    var (sub, subWire) = r.Tag();
                    if (sub == 1) level["price"] = r.Fixed64();
                    else if (sub == 2) level["quantity"] = (long)r.Fixed64();
                    else r.Skip(subWire);
                }

                r.Position = subEnd;
            }
            else
            {
                r.Skip(wire);
            }
        }

        r.Position = end;
        return level;
    }

    private static JsonObject Index(ref Reader r)
    {
        var length = (int)Math.Min(r.Varint(), int.MaxValue / 2);
        var end = r.Position + length;
        var result = new JsonObject();
        while (r.Position < end)
        {
            var (field, wire) = r.Tag();
            if (field == 0) break;
            if (field == 1) result["timestamp"] = r.Fixed64();
            else if (field == 2) result["value"] = r.Fixed64();
            else r.Skip(wire);
        }

        r.Position = end;
        return result;
    }
}
