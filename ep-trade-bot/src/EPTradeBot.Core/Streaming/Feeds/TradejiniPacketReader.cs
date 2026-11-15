using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>One packet of Tradejini's stream, read: its kind (L1 for last price and quote, L5 for depth), its fields by name and, for depth, the two sides.</summary>
public sealed record TradejiniMessage(string Type, Dictionary<string, JsonNode?> Data, List<Dictionary<string, JsonNode?>> Bids, List<Dictionary<string, JsonNode?>> Asks);

/// <summary>
/// The binary frames of Tradejini's stream (api/nxtradstream.py): a header (length, version 1, compression: 100 is zlib) and packets, each a length, a type and key and value fields in the broker's units (prices as integers, a divisor apart by segment).
/// The last price packets of a scrip are merged into one record. A frame or packet it cannot read ends the frame there; the packets before it stand.
/// </summary>
public sealed class TradejiniPacketReader
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

    private sealed class DecodeStop : Exception;

    private readonly Dictionary<string, Dictionary<string, JsonNode?>> l1Cache = [];
    private int eventLength = 100;

    /// <summary>Forgets the merged last price records (a cancel of the whole feed does).</summary>
    public void ClearL1() => l1Cache.Clear();

    /// <summary>Every packet of the frame that carries a last price or a depth, in order, however far the frame could be read.</summary>
    public List<TradejiniMessage> Read(ReadOnlySpan<byte> frame)
    {
        var messages = new List<TradejiniMessage>();
        try
        {
            Frame(frame, messages);
        }
        catch (Exception ex) when (ex is DecodeStop or InvalidDataException or ArgumentOutOfRangeException or InvalidCastException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
        }

        return messages;
    }

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

    private void Frame(ReadOnlySpan<byte> message, List<TradejiniMessage> messages)
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
            Packet(payload.AsSpan(index, end - index), length, messages);
            index += length;
        }
    }

    private void Packet(ReadOnlySpan<byte> data, int length, List<TradejiniMessage> messages)
    {
        if (data.Length < 3) throw new DecodeStop();
        var type = (sbyte)data[2];
        if (Table(type) is not { } found) return;
        switch (type)
        {
            case 10:
                if (DecodeL1(data, length) is { } l1) messages.Add(new TradejiniMessage("L1", new Dictionary<string, JsonNode?>(l1), [], []));
                break;
            case 11:
                if (DecodeL5(data, length, out var bids, out var asks) is { } l5) messages.Add(new TradejiniMessage("L5", l5, bids, asks));
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

}
