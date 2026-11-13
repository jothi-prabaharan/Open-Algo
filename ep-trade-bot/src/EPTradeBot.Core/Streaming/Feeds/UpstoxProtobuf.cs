using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Reads Upstox's market-data protobuf (MarketDataFeedV3) into the same shape OpenAlgo gets from <c>MessageToDict</c>: camelCase names, 64-bit integers as
/// text, enums by name, a number or text left at its default left out, a wrapped double flattened to the number, a map as an object. The schema is the
/// published .proto, small enough to hold as a table, so no protobuf library is needed.
/// </summary>
public static class UpstoxProtobuf
{
    private enum Kind { Double, Int64, Bool, String, Enum, Message, Wrapper }

    private sealed record Field(string Name, Kind Kind, string? Type = null, bool Repeated = false, bool Map = false);

    private static readonly Dictionary<string, Dictionary<int, Field>> Schema = new()
    {
        ["FeedResponse"] = new()
        {
            [1] = new("type", Kind.Enum, "Type"), [2] = new("feeds", Kind.Message, "FeedsEntry", Repeated: true, Map: true),
            [3] = new("currentTs", Kind.Int64), [4] = new("marketInfo", Kind.Message, "MarketInfo"),
        },
        ["FeedsEntry"] = new() { [1] = new("key", Kind.String), [2] = new("value", Kind.Message, "Feed") },
        ["Feed"] = new()
        {
            [1] = new("ltpc", Kind.Message, "LTPC"), [2] = new("fullFeed", Kind.Message, "FullFeed"),
            [3] = new("firstLevelWithGreeks", Kind.Message, "FirstLevelWithGreeks"), [4] = new("requestMode", Kind.Enum, "RequestMode"),
        },
        ["LTPC"] = new()
        {
            [1] = new("ltp", Kind.Double), [2] = new("ltt", Kind.Int64), [3] = new("ltq", Kind.Int64), [4] = new("cp", Kind.Double), [5] = new("iep", Kind.Wrapper),
        },
        ["FullFeed"] = new() { [1] = new("marketFF", Kind.Message, "MarketFullFeed"), [2] = new("indexFF", Kind.Message, "IndexFullFeed") },
        ["MarketFullFeed"] = new()
        {
            [1] = new("ltpc", Kind.Message, "LTPC"), [2] = new("marketLevel", Kind.Message, "MarketLevel"), [3] = new("optionGreeks", Kind.Message, "OptionGreeks"),
            [4] = new("marketOHLC", Kind.Message, "MarketOHLC"), [5] = new("atp", Kind.Double), [6] = new("vtt", Kind.Int64), [7] = new("oi", Kind.Double),
            [8] = new("iv", Kind.Double), [9] = new("tbq", Kind.Double), [10] = new("tsq", Kind.Double), [11] = new("iep", Kind.Double), [12] = new("rp", Kind.Double),
            [13] = new("ieq", Kind.Int64), [14] = new("iiqTotal", Kind.Int64), [15] = new("iiqM", Kind.Int64), [16] = new("casEligible", Kind.Bool),
        },
        ["IndexFullFeed"] = new() { [1] = new("ltpc", Kind.Message, "LTPC"), [2] = new("marketOHLC", Kind.Message, "MarketOHLC") },
        ["MarketLevel"] = new() { [1] = new("bidAskQuote", Kind.Message, "Quote", Repeated: true) },
        ["Quote"] = new() { [1] = new("bidQ", Kind.Int64), [2] = new("bidP", Kind.Double), [3] = new("askQ", Kind.Int64), [4] = new("askP", Kind.Double) },
        ["MarketOHLC"] = new() { [1] = new("ohlc", Kind.Message, "OHLC", Repeated: true) },
        ["OHLC"] = new()
        {
            [1] = new("interval", Kind.String), [2] = new("open", Kind.Double), [3] = new("high", Kind.Double), [4] = new("low", Kind.Double),
            [5] = new("close", Kind.Double), [6] = new("vol", Kind.Int64), [7] = new("ts", Kind.Int64),
        },
        ["OptionGreeks"] = new()
        {
            [1] = new("delta", Kind.Double), [2] = new("theta", Kind.Double), [3] = new("gamma", Kind.Double), [4] = new("vega", Kind.Double), [5] = new("rho", Kind.Double),
        },
        ["FirstLevelWithGreeks"] = new()
        {
            [1] = new("ltpc", Kind.Message, "LTPC"), [2] = new("firstDepth", Kind.Message, "Quote"), [3] = new("optionGreeks", Kind.Message, "OptionGreeks"),
            [4] = new("vtt", Kind.Int64), [5] = new("oi", Kind.Double), [6] = new("iv", Kind.Double),
        },
        ["MarketInfo"] = new()
        {
            [1] = new("segmentStatus", Kind.Message, "SegmentStatusEntry", Repeated: true, Map: true),
            [2] = new("casMarketStatus", Kind.Message, "StatusEntry", Repeated: true, Map: true),
            [3] = new("preOpenSessionStatus", Kind.Message, "StatusEntry", Repeated: true, Map: true),
        },
        ["SegmentStatusEntry"] = new() { [1] = new("key", Kind.String), [2] = new("value", Kind.Enum, "MarketStatus") },
        ["StatusEntry"] = new() { [1] = new("key", Kind.String), [2] = new("value", Kind.Message, "StatusInfo") },
        ["StatusInfo"] = new() { [1] = new("status", Kind.String), [2] = new("updatedTime", Kind.Int64) },
    };

    private static readonly Dictionary<string, string[]> Enums = new()
    {
        ["Type"] = ["initial_feed", "live_feed", "market_info"],
        ["RequestMode"] = ["ltpc", "full_d5", "option_greeks", "full_d30"],
        ["MarketStatus"] = ["PRE_OPEN_START", "PRE_OPEN_END", "NORMAL_OPEN", "NORMAL_CLOSE", "CLOSING_START", "CLOSING_END"],
    };

    /// <summary>The frame as a dictionary, or an exception when it is not a well-formed FeedResponse.</summary>
    public static JsonObject DecodeFeedResponse(ReadOnlySpan<byte> frame) => Message("FeedResponse", frame);

    private static JsonObject Message(string type, ReadOnlySpan<byte> bytes)
    {
        var fields = Schema[type];
        var result = new JsonObject();
        var position = 0;
        while (position < bytes.Length)
        {
            var tag = Varint(bytes, ref position);
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (number == 0) throw new FormatException("A field number of zero.");

            ReadOnlySpan<byte> payload = default;
            ulong scalar = 0;
            switch (wire)
            {
                case 0: scalar = Varint(bytes, ref position); break;
                case 1:
                    if (bytes.Length - position < 8) throw new FormatException("Cut short.");
                    scalar = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(position, 8));
                    position += 8;
                    break;
                case 2:
                    var length = Varint(bytes, ref position);
                    if (length > (ulong)(bytes.Length - position)) throw new FormatException("Cut short.");
                    payload = bytes.Slice(position, (int)length);
                    position += (int)length;
                    break;
                case 5:
                    if (bytes.Length - position < 4) throw new FormatException("Cut short.");
                    scalar = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
                    position += 4;
                    break;
                default: throw new FormatException("A wire type this schema does not use.");
            }

            if (!fields.TryGetValue(number, out var field)) continue;
            var value = Value(field, wire, scalar, payload);
            if (value is null && field.Kind != Kind.Message && !field.Map) continue;

            if (field.Map)
            {
                var map = result[field.Name] as JsonObject ?? new JsonObject();
                result[field.Name] = map;
                var entry = (JsonObject)value!;
                map[entry["key"] is JsonValue k && k.TryGetValue<string>(out var key) ? key : ""] = entry["value"] is { } v ? v.DeepClone() : DefaultOf(field, type);
            }
            else if (field.Repeated)
            {
                var list = result[field.Name] as JsonArray ?? new JsonArray();
                result[field.Name] = list;
                list.Add(value);
            }
            else
            {
                result[field.Name] = value;
            }
        }

        return result;
    }

    /// <summary>A map entry with no value on the wire reads as the value type's default.</summary>
    private static JsonNode? DefaultOf(Field mapField, string parent)
    {
        var entryFields = Schema[mapField.Type!];
        var valueField = entryFields[2];
        return valueField.Kind switch
        {
            Kind.Message => new JsonObject(),
            Kind.Enum => Enums[valueField.Type!][0],
            _ => null,
        };
    }

    private static JsonNode? Value(Field field, int wire, ulong scalar, ReadOnlySpan<byte> payload)
    {
        switch (field.Kind)
        {
            case Kind.Double:
                if (wire != 1) throw new FormatException("A double with the wrong wire type.");
                var d = BitConverter.UInt64BitsToDouble(scalar);
                return d == 0 ? null : JsonValue.Create(d);
            case Kind.Int64:
                if (wire != 0) throw new FormatException("A number with the wrong wire type.");
                return scalar == 0 ? null : JsonValue.Create(((long)scalar).ToString(System.Globalization.CultureInfo.InvariantCulture));
            case Kind.Bool:
                if (wire != 0) throw new FormatException("A flag with the wrong wire type.");
                return scalar == 0 ? null : JsonValue.Create(true);
            case Kind.String:
                if (wire != 2) throw new FormatException("Text with the wrong wire type.");
                var text = new UTF8Encoding(false, true).GetString(payload);
                return text.Length == 0 ? null : JsonValue.Create(text);
            case Kind.Enum:
                if (wire != 0) throw new FormatException("An enum with the wrong wire type.");
                var names = Enums[field.Type!];
                return scalar == 0 ? (field.Map ? JsonValue.Create(names[0]) : null) : scalar < (ulong)names.Length ? JsonValue.Create(names[scalar]) : JsonValue.Create((long)scalar);
            case Kind.Wrapper:
                if (wire != 2) throw new FormatException("A wrapper with the wrong wire type.");
                var inner = Message("Wrapper", payload);
                return inner["value"]?.DeepClone() ?? JsonValue.Create(0.0);
            default:
                if (wire != 2) throw new FormatException("A message with the wrong wire type.");
                return Message(field.Type!, payload);
        }
    }

    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (position >= bytes.Length) throw new FormatException("Cut short.");
            var b = bytes[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }

        throw new FormatException("A number that does not end.");
    }

    static UpstoxProtobuf() => Schema["Wrapper"] = new() { [1] = new("value", Kind.Double) };
}
