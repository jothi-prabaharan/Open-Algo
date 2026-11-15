using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Pocketful's market data packets (broker/pocketful/api/packet_decoder.py). The first byte is the mode: 1 detailed (102 bytes), 2 compact (42 bytes), 4 snapquote (166 bytes), all big-endian. A text frame may carry the same fields as JSON,
/// directly or inside "d" or "data", for compact and snapquote data only; detailed data has no text form, and a packet that is too short, or a text frame that is no packet, decodes to nothing.
/// </summary>
public static class PocketfulPackets
{
    public const int Detailed = 1, Compact = 2, Snapquote = 4;

    private static JsonArray Five(ReadOnlySpan<byte> frame, int at, bool wide = false)
    {
        var values = new JsonArray();
        for (var i = 0; i < 5; i++) values.Add((long)BinaryPrimitives.ReadUInt32BigEndian(frame[(at + 4 * i)..]));
        return values;
    }

    /// <summary>The mode of a frame as OpenAlgo reads it: the first byte of a binary frame, or the "mode" of a text frame that is a JSON object. Null when it cannot be told.</summary>
    public static int? ModeOf(ReadOnlySpan<byte> binary) => binary.Length == 0 ? null : (sbyte)binary[0];

    public static JsonNode? ModeOfText(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject o && o.TryGetPropertyValue("mode", out var mode) ? mode : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject Common(ReadOnlySpan<byte> f) => new()
    {
        ["mode"] = (sbyte)f[0], ["exchange_code"] = (sbyte)f[1], ["instrument_token"] = (long)BinaryPrimitives.ReadUInt32BigEndian(f[2..]),
    };

    private static long U32(ReadOnlySpan<byte> f, int at) => BinaryPrimitives.ReadUInt32BigEndian(f[at..]);

    private static JsonObject Aliased(JsonObject o)
    {
        if (o.ContainsKey("instrumentToken") && !o.ContainsKey("instrument_token")) o["instrument_token"] = o["instrumentToken"]?.DeepClone();
        if (o.ContainsKey("exchangeCode") && !o.ContainsKey("exchange_code")) o["exchange_code"] = o["exchangeCode"]?.DeepClone();
        return o;
    }

    private static bool HasToken(JsonObject o) => o.ContainsKey("instrument_token") || o.ContainsKey("instrumentToken");

    /// <summary>The JSON form of a packet, as OpenAlgo's decoder finds it; null when the text holds none.</summary>
    private static JsonObject? FromText(string text, bool allowList)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is JsonObject o)
        {
            if (HasToken(o)) return Aliased(o);
            if (o["d"] is JsonObject d) return Aliased(d);
            if (o["data"] is JsonObject data) return Aliased(data);
        }
        else if (allowList && node is JsonArray { Count: > 0 } list && list[0] is JsonObject first && HasToken(first)) return Aliased(first);
        return null;
    }

    public static JsonObject DecodeDetailed(ReadOnlySpan<byte> f)
    {
        if (f.Length < 102) return [];
        var r = Common(f);
        r["last_traded_price"] = U32(f, 6); r["last_traded_time"] = U32(f, 10); r["last_traded_quantity"] = U32(f, 14); r["trade_volume"] = U32(f, 18);
        r["best_bid_price"] = U32(f, 22); r["best_bid_quantity"] = U32(f, 26); r["best_ask_price"] = U32(f, 30); r["best_ask_quantity"] = U32(f, 34);
        // Eight bytes each, which OpenAlgo reads as unsigned.
        r["total_buy_quantity"] = BinaryPrimitives.ReadUInt64BigEndian(f[38..]); r["total_sell_quantity"] = BinaryPrimitives.ReadUInt64BigEndian(f[46..]);
        r["average_trade_price"] = U32(f, 54); r["exchange_timestamp"] = U32(f, 58); r["open_price"] = U32(f, 62); r["high_price"] = U32(f, 66); r["low_price"] = U32(f, 70); r["close_price"] = U32(f, 74);
        r["yearly_high_price"] = U32(f, 78); r["yearly_low_price"] = U32(f, 82); r["lowDPR"] = U32(f, 86); r["highDPR"] = U32(f, 90); r["currentOpenInterest"] = U32(f, 94); r["initialOpenInterest"] = U32(f, 98);
        return r;
    }

    public static JsonObject DecodeCompact(ReadOnlySpan<byte> f)
    {
        if (f.Length < 42) return [];
        var r = Common(f);
        r["last_traded_price"] = U32(f, 6); r["change"] = U32(f, 10); r["last_traded_time"] = U32(f, 14); r["lowDPR"] = U32(f, 18); r["highDPR"] = U32(f, 22);
        r["currentOpenInterest"] = U32(f, 26); r["initialOpenInterest"] = U32(f, 30); r["bidPrice"] = U32(f, 34); r["askPrice"] = U32(f, 38);
        return r;
    }

    public static JsonObject DecodeCompact(string text) => FromText(text, false) ?? [];

    public static JsonObject DecodeSnapquote(ReadOnlySpan<byte> f)
    {
        if (f.Length < 166) return [];
        var r = Common(f);
        r["buyers"] = Five(f, 6); r["bidPrices"] = Five(f, 26); r["bidQtys"] = Five(f, 46); r["sellers"] = Five(f, 66); r["askPrices"] = Five(f, 86); r["askQtys"] = Five(f, 106);
        r["averageTradePrice"] = U32(f, 126); r["open"] = U32(f, 130); r["high"] = U32(f, 134); r["low"] = U32(f, 138); r["close"] = U32(f, 142);
        r["totalBuyQty"] = BinaryPrimitives.ReadUInt64BigEndian(f[146..]); r["totalSellQty"] = BinaryPrimitives.ReadUInt64BigEndian(f[154..]); r["volume"] = U32(f, 162);
        return r;
    }

    public static JsonObject DecodeSnapquote(string text) => FromText(text, true) ?? [];
}
