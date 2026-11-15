using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Tradejini's stream (broker/tradejini/streaming and api/nxtradstream.py): one address that carries the token, no sign-in frame. A request names a whole feed (L1 for last price and quote, L5 for depth) and REPLACES the list the server holds for it,
/// so every change sends the complete list of that feed, and a feed with nothing left is cancelled whole. A frame is a header (length, version 1, compression: 100 is zlib) and packets, each a length, a type and key and value fields in the broker's
/// units (prices as integers, a divisor apart by segment). The last price packets of a scrip are merged into one record the next quote is built from; a depth packet carries no prices of the scrip's own, so the depth tick has zeros for them. A packet
/// the stream cannot read ends the frame at that packet, and the ticks before it stand.
/// </summary>
public sealed class TradejiniFeedCodec : IFeedCodec
{
    private sealed record Seg(string Name, double Divisor);

    private static readonly Dictionary<int, Seg> Segments = new()
    {
        [1] = new("NSE", 100), [2] = new("BSE", 100), [3] = new("NFO", 100), [4] = new("BFO", 100), [5] = new("CDS", 10000000), [6] = new("BCD", 10000), [7] = new("MCD", 10000), [8] = new("MCX", 100), [9] = new("NCO", 10000), [10] = new("BCO", 10000),
    };

    private enum Fmt { None, Divide, Hundred, Time }

    private sealed record Spec(string Key, char Kind, int Len, Fmt Format = Fmt.None);

    private static Dictionary<int, Spec> Common(params (int Key, Spec Spec)[] more)
    {
        var map = new Dictionary<int, Spec>();
        foreach (var (key, spec) in more) map[key] = spec;
        return map;
    }

    private static Spec I(string key, Fmt format = Fmt.None) => new(key, 'i', 4, format);

    private static Spec U(string key) => new(key, 'I', 4);

    private static readonly Dictionary<int, Spec> L1Spec = Common(
        (26, new("exchSeg", 'B', 1)), (27, I("token")), (28, new("precision", '?', 1)), (29, I("ltp", Fmt.Divide)), (30, I("open", Fmt.Divide)), (31, I("high", Fmt.Divide)), (32, I("low", Fmt.Divide)), (33, I("close", Fmt.Divide)),
        (34, I("chng", Fmt.Divide)), (35, I("chngPer", Fmt.Hundred)), (36, I("atp", Fmt.Divide)), (37, I("yHigh", Fmt.Divide)), (38, I("yLow", Fmt.Divide)), (39, U("ltq")), (40, U("vol")), (41, new("ttv", 'd', 8)),
        (42, I("ucl", Fmt.Divide)), (43, I("lcl", Fmt.Divide)), (44, U("OI")), (45, I("OIChngPer", Fmt.Hundred)), (46, I("ltt", Fmt.Time)), (49, I("bidPrice", Fmt.Divide)), (50, U("qty")), (51, U("no")), (52, I("askPrice", Fmt.Divide)),
        (53, U("qty")), (54, U("no")), (55, new("nDepth", 'B', 1)), (56, new("nLen", 'H', 2)), (58, U("prevOI")), (59, U("dayHighOI")), (60, U("dayLowOI")), (70, I("spotPrice", Fmt.Divide)), (71, I("dayClose", Fmt.Divide)), (74, I("vwap", Fmt.Divide)));

    private static readonly Dictionary<int, Spec> L5Spec = Common(
        (26, new("exchSeg", 'B', 1)), (27, I("token")), (28, new("precision", 'B', 1)), (47, U("totBuyQty")), (48, U("totSellQty")), (49, I("price", Fmt.Divide)), (50, U("qty")), (51, U("no")), (52, I("price", Fmt.Divide)), (53, U("qty")),
        (54, U("no")), (55, new("nDepth", 'B', 1)));

    private static readonly Dictionary<int, Spec> OhlcSpec = Common(
        (26, new("exchSeg", 'B', 1)), (27, I("token")), (28, new("precision", 'B', 1)), (30, I("open", Fmt.Divide)), (31, I("high", Fmt.Divide)), (32, I("low", Fmt.Divide)), (33, I("close", Fmt.Divide)), (40, U("vol")), (46, I("time", Fmt.Time)),
        (74, I("vwap", Fmt.Divide)), (75, new("type", 's', 4)), (76, U("minuteOi")));

    private static readonly Dictionary<int, Spec> AuthSpec = Common((25, new("auth_status", 'B', 1)));

    private static readonly Dictionary<int, Spec> StatusSpec = Common((56, new("nLen", 'H', 2)), (26, new("exchSeg", 'B', 1)), (57, new("marketStatus", 'B', 1)));

    private static readonly Dictionary<int, Spec> EventSpec = Common((56, new("nLen", 'H', 2)), (61, new("message", 's', 100)));

    private static readonly Dictionary<int, Spec> PingSpec = Common((62, new("pong", 'B', 1)));

    private static readonly Dictionary<int, Spec> GreeksSpec = Common(
        (26, new("exchSeg", 'B', 1)), (27, I("token")), (63, new("itm", 'd', 8)), (64, new("iv", 'd', 8)), (65, new("delta", 'd', 8)), (66, new("gamma", 'd', 8)), (67, new("theta", 'd', 8)), (68, new("rho", 'd', 8)),
        (69, new("vega", 'd', 8)), (72, new("highiv", 'd', 8)), (73, new("lowiv", 'd', 8)));

    private sealed record Sub(string Symbol, string Exchange, string Token, string BrokerExchange, int Mode);

    private sealed class DecodeStop : Exception;

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly Dictionary<string, Dictionary<string, JsonNode?>> l1Cache = [];
    private int eventLength = 100;

    public string Broker => "tradejini";

    private static string Feed(int mode) => mode is 1 or 2 ? "L1" : "L5";

    private List<string> Tokens(string feed)
    {
        var tokens = new List<string>();
        foreach (var sub in subs.Values)
        {
            if ((feed == "L1") != (sub.Mode is 1 or 2)) continue;
            var text = $"{sub.Token}_{sub.BrokerExchange}";
            if (!tokens.Contains(text)) tokens.Add(text);
        }

        return tokens;
    }

    /// <summary>The complete list of a feed, or the cancel of it when it is empty and a cancel is wanted.</summary>
    private string? Sync(string feed, bool cancelWhenEmpty)
    {
        var tokens = Tokens(feed);
        if (tokens.Count == 0)
        {
            if (!cancelWhenEmpty) return null;
            if (feed == "L1") l1Cache.Clear();
            return new JsonObject { ["type"] = feed, ["action"] = "unsub" }.ToJsonString() + "\n";
        }

        return new JsonObject { ["type"] = feed, ["action"] = "sub", ["tokens"] = new JsonArray(tokens.Select(t => (JsonNode?)new JsonObject { ["t"] = t }).ToArray()) }.ToJsonString() + "\n";
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var dirty = new List<string>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3) continue;
                subs[$"{r.Symbol}_{r.Exchange}_{mode}"] = new Sub(r.Symbol, r.Exchange, r.Token, r.BrokerExchange, mode);
                if (!dirty.Contains(Feed(mode))) dirty.Add(Feed(mode));
            }

            return dirty.Select(f => Sync(f, true)).OfType<string>().ToList();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var dirty = new List<string>();
            foreach (var m in mode is { } one ? [one] : Enum.GetValues<StreamMode>())
            {
                subs.Remove($"{target.Symbol}_{target.Exchange}_{(int)m}");
                if (!dirty.Contains(Feed((int)m))) dirty.Add(Feed((int)m));
            }

            return dirty.Select(f => Sync(f, true)).OfType<string>().ToList();
        }
    }

    /// <summary>A new connection: each feed that holds anything is sent whole. There is nothing to cancel on a connection that is new.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return new[] { "L1", "L5" }.Select(f => Sync(f, false)).OfType<string>().ToList();
    }

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            l1Cache.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- reading -----------------------------------------------------------------------------------------------

    private static (JsonNode? Value, int Len) Read(Spec spec, ReadOnlySpan<byte> data, int at)
    {
        if (spec.Kind == '?') throw new DecodeStop();
        var length = spec.Len;
        if (at + length > data.Length) throw new DecodeStop();
        var slice = data.Slice(at, length);
        return spec.Kind switch
        {
            'B' => (JsonValue.Create((long)slice[0]), length),
            'H' => (JsonValue.Create((long)BinaryPrimitives.ReadUInt16LittleEndian(slice)), length),
            'i' => (JsonValue.Create((long)BinaryPrimitives.ReadInt32LittleEndian(slice)), length),
            'I' => (JsonValue.Create((long)BinaryPrimitives.ReadUInt32LittleEndian(slice)), length),
            'd' => (JsonValue.Create(BinaryPrimitives.ReadDoubleLittleEndian(slice)), length),
            _ => (JsonValue.Create(Encoding.UTF8.GetString(slice).TrimEnd('\0')), length),
        };
    }

    private static string Time(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static JsonNode? Apply(Spec spec, JsonNode? raw, double divisor) => spec.Format switch
    {
        Fmt.Divide => JsonValue.Create(raw!.GetValue<long>() / divisor),
        Fmt.Hundred => JsonValue.Create(raw!.GetValue<long>() / 100.0),
        _ => raw,
    };

    private static (Dictionary<int, Spec> Table, string Name)? Table(int type) => type switch
    {
        10 => (L1Spec, "L1"), 11 => (L5Spec, "L5"), 12 => (OhlcSpec, "OHLC"), 13 => (AuthSpec, "auth"), 14 => (StatusSpec, "marketStatus"), 15 => (EventSpec, "EVENTS"), 16 => (PingSpec, "PING"), 17 => (GreeksSpec, "greeks"), _ => null,
    };

    /// <summary>The fields of a last price packet (and of the other kinds that are read the same way): the segment sets the divisor, the others are held until it is known.</summary>
    private static Dictionary<string, JsonNode?> ReadFields(Dictionary<int, Spec> table, ReadOnlySpan<byte> data, int length, Func<int, Spec, JsonNode?, bool>? special = null)
    {
        var result = new Dictionary<string, JsonNode?>();
        var raw = new List<(Spec Spec, JsonNode? Value)>();
        double divisor = 100;
        var known = false;
        var idx = 3;
        while (idx < length)
        {
            if (idx >= data.Length) throw new DecodeStop();
            var key = data[idx];
            idx++;
            if (!table.TryGetValue(key, out var spec)) throw new DecodeStop();
            var (value, len) = Read(spec, data, idx);
            if (spec.Key == "exchSeg")
            {
                if (!Segments.TryGetValue((int)value!.GetValue<long>(), out var seg)) throw new DecodeStop();
                divisor = seg.Divisor;
                known = true;
                result[spec.Key] = seg.Name;
            }
            else if (spec.Format == Fmt.Time)
            {
                result[spec.Key] = Time(value!.GetValue<long>());
            }
            else
            {
                raw.Add((spec, value));
            }

            idx += len;
        }

        if (known)
            foreach (var (spec, value) in raw) result[spec.Key] = Apply(spec, value, divisor);
        return result;
    }

    private static string SymbolOf(Dictionary<string, JsonNode?> data)
    {
        if (!data.TryGetValue("token", out var token) || !data.TryGetValue("exchSeg", out var seg)) throw new DecodeStop();
        return $"{token!.GetValue<long>()}_{seg!.GetValue<string>()}";
    }

    private Dictionary<string, JsonNode?>? DecodeL1(ReadOnlySpan<byte> data, int length)
    {
        var fields = ReadFields(L1Spec, data, length);
        var symbol = SymbolOf(fields);
        fields["symbol"] = symbol;
        fields["msgType"] = "L1";
        if (l1Cache.TryGetValue(symbol, out var cached))
        {
            foreach (var (key, value) in fields) cached[key] = value?.DeepClone();
            return cached;
        }

        l1Cache[symbol] = fields.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());
        return l1Cache[symbol];
    }

    private static Dictionary<string, JsonNode?>? DecodeL5(ReadOnlySpan<byte> data, int length, out List<Dictionary<string, JsonNode?>> bids, out List<Dictionary<string, JsonNode?>> asks)
    {
        bids = [];
        asks = [];
        var result = new Dictionary<string, JsonNode?>();
        var raw = new List<(Spec Spec, JsonNode? Value)>();
        double divisor = 100;
        var known = false;
        long levels = 0;
        List<Dictionary<string, JsonNode?>>? list = null;
        var level = new Dictionary<string, JsonNode?>();
        var idx = 3;
        while (idx < length)
        {
            if (idx >= data.Length) throw new DecodeStop();
            var key = data[idx];
            idx++;
            if (!L5Spec.TryGetValue(key, out var spec)) throw new DecodeStop();
            var (value, len) = Read(spec, data, idx);
            if (spec.Key == "nDepth")
            {
                levels = value!.GetValue<long>();
                list = bids;
            }
            else if (spec.Key == "exchSeg")
            {
                if (!Segments.TryGetValue((int)value!.GetValue<long>(), out var seg)) throw new DecodeStop();
                divisor = seg.Divisor;
                known = true;
                result[spec.Key] = seg.Name;
            }
            else if (list is not null)
            {
                level[spec.Key] = Apply(spec, value, divisor);
            }
            else
            {
                raw.Add((spec, value));
            }

            if (list is not null)
            {
                if (level.Count == 3)
                {
                    list.Add(level);
                    level = [];
                }

                if (levels == list.Count) list = asks;
            }

            idx += len;
        }

        if (known)
            foreach (var (spec, value) in raw) result[spec.Key] = Apply(spec, value, divisor);
        result["symbol"] = SymbolOf(result);
        result["msgType"] = "L5";
        return result;
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var published = new List<FeedPublish>();
        lock (gate)
        {
            try
            {
                Frame(frame, nowMs, published);
            }
            catch (Exception ex) when (ex is DecodeStop or InvalidDataException or ArgumentOutOfRangeException or InvalidCastException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                // A frame or packet the stream cannot read ends the frame there; what was published before it stands.
            }
        }

        return published;
    }

    private void Frame(ReadOnlySpan<byte> message, long nowMs, List<FeedPublish> published)
    {
        if (message.Length < 6) throw new DecodeStop();
        if ((sbyte)message[4] != 1) return;
        byte[] payload;
        if ((sbyte)message[5] == 100)
        {
            using var input = new MemoryStream(message[6..].ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            payload = output.ToArray();
        }
        else
        {
            payload = message[6..].ToArray();
        }

        var index = 0;
        while (index < payload.Length)
        {
            if (index + 2 > payload.Length) throw new DecodeStop();
            int length = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(index, 2));
            if (length <= 0) break;
            var end = Math.Min(payload.Length, index + length);
            Packet(payload.AsSpan(index, end - index), length, nowMs, published);
            index += length;
        }
    }

    private void Packet(ReadOnlySpan<byte> data, int length, long nowMs, List<FeedPublish> published)
    {
        if (data.Length < 3) throw new DecodeStop();
        var type = (sbyte)data[2];
        if (Table(type) is not { } found) return;
        switch (type)
        {
            case 10:
                if (DecodeL1(data, length) is { } l1) Publish(l1, "L1", [], [], nowMs, published);
                break;
            case 11:
                if (DecodeL5(data, length, out var bids, out var asks) is { } l5) Publish(l5, "L5", bids, asks, nowMs, published);
                break;
            case 15:
                Walk(found.Table, data, length, events: true);
                break;
            case 14:
                Walk(found.Table, data, length, events: false);
                break;
            case 12 or 17:
                _ = ReadFields(found.Table, data, length);
                break;
            default:
                Walk(found.Table, data, length, events: false);
                break;
        }
    }

    /// <summary>Reads a packet whose fields are not published, so that a field or length the stream cannot read ends the frame as it does for OpenAlgo.</summary>
    private void Walk(Dictionary<int, Spec> table, ReadOnlySpan<byte> data, int length, bool events)
    {
        var idx = 3;
        while (idx < length)
        {
            if (idx >= data.Length) throw new DecodeStop();
            var key = data[idx];
            idx++;
            if (!table.TryGetValue(key, out var spec)) throw new DecodeStop();
            var width = events && spec.Key == "message" ? eventLength : spec.Len;
            var (value, len) = events && spec.Key == "message" ? ReadString(data, idx, width) : Read(spec, data, idx);
            if (events && spec.Key == "nLen") eventLength = (int)value!.GetValue<long>();
            idx += len;
        }
    }

    private static (JsonNode? Value, int Len) ReadString(ReadOnlySpan<byte> data, int at, int length)
    {
        if (at + length > data.Length) throw new DecodeStop();
        return (JsonValue.Create(Encoding.UTF8.GetString(data.Slice(at, length)).TrimEnd('\0')), length);
    }

    // ---- publishing --------------------------------------------------------------------------------------------

    private static JsonNode? Get(Dictionary<string, JsonNode?> data, string key, JsonNode? fallback) => data.TryGetValue(key, out var v) ? v?.DeepClone() : fallback;

    private static JsonNode? Zero => JsonValue.Create(0);

    private static JsonArray Depth(List<Dictionary<string, JsonNode?>> side)
    {
        var rows = new JsonArray();
        foreach (var level in side)
            rows.Add(new JsonObject { ["price"] = Get(level, "price", Zero), ["quantity"] = Get(level, "qty", Zero), ["orders"] = Get(level, "no", Zero) });
        if (rows.Count == 0)
            for (var i = 0; i < 5; i++) rows.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 });
        return rows;
    }

    private void Publish(Dictionary<string, JsonNode?> message, string type, List<Dictionary<string, JsonNode?>> bids, List<Dictionary<string, JsonNode?>> asks, long nowMs, List<FeedPublish> published)
    {
        var symbol = message["symbol"]!.GetValue<string>();
        var parts = symbol.Split('_');
        if (parts.Length != 2) return;
        foreach (var sub in subs.Values.Where(s => s.Token == parts[0] && s.BrokerExchange == parts[1]).ToList())
        {
            var publish = type == "L1" ? sub.Mode is 1 or 2 : sub.Mode == 3;
            if (!publish) continue;
            JsonObject data;
            switch (sub.Mode)
            {
                case 1:
                    data = new JsonObject { ["ltp"] = Get(message, "ltp", Zero), ["ltt"] = Get(message, "ltt", JsonValue.Create("")) };
                    break;
                case 2:
                    data = new JsonObject
                    {
                        ["ltp"] = Get(message, "ltp", Zero), ["ltt"] = Get(message, "ltt", null), ["volume"] = Get(message, "vol", Zero), ["open"] = Get(message, "open", Zero), ["high"] = Get(message, "high", Zero), ["low"] = Get(message, "low", Zero),
                        ["close"] = Get(message, "close", Zero), ["last_quantity"] = Get(message, "ltq", Zero), ["average_price"] = Get(message, "atp", Zero), ["total_buy_quantity"] = Get(message, "totBuyQty", Zero),
                        ["total_sell_quantity"] = Get(message, "totSellQty", Zero), ["oi"] = Get(message, "OI", Zero), ["change"] = Get(message, "chng", Zero), ["change_percent"] = Get(message, "chngPer", Zero),
                    };
                    break;
                default:
                    data = new JsonObject
                    {
                        ["ltp"] = Get(message, "ltp", Zero), ["ltt"] = Get(message, "ltt", null), ["volume"] = Get(message, "vol", Zero), ["open"] = Get(message, "open", Zero), ["high"] = Get(message, "high", Zero), ["low"] = Get(message, "low", Zero),
                        ["close"] = Get(message, "close", Zero), ["oi"] = Get(message, "OI", Zero), ["upper_circuit"] = Get(message, "ucl", Zero), ["lower_circuit"] = Get(message, "lcl", Zero), ["total_buy_quantity"] = Get(message, "totBuyQty", Zero),
                        ["total_sell_quantity"] = Get(message, "totSellQty", Zero), ["depth"] = new JsonObject { ["buy"] = Depth(bids), ["sell"] = Depth(asks) },
                    };
                    break;
            }

            data["symbol"] = sub.Symbol;
            data["exchange"] = sub.Exchange;
            data["mode"] = sub.Mode;
            data["timestamp"] = nowMs;
            published.Add(new FeedPublish($"{sub.Exchange}_{sub.Symbol}_{sub.Mode switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" }}", data));
        }
    }
}
