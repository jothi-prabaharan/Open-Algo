using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Fyers' HSM market-data socket (broker/fyers/streaming: fyers_hsm_websocket.py, fyers_adapter.py, fyers_mapping.py, fyers_websocket_adapter.py). Everything on the wire is binary:
/// the sign-in carries a key found inside the access token, a subscription is a list of feed names ("sf|nse_cm|3045" for a scrip, "dp|" for its five-level depth, "if|" for an
/// index), and the answers are a first snapshot per feed followed by updates that carry only the fields that changed, as 32-bit integers to be scaled by the feed's own
/// multiplier and precision. Fyers has no unsubscribe message: a symbol that is dropped simply stops being published. Subscribing waits for the sign-in to be accepted.
/// </summary>
public sealed class FyersFeedCodec : IFeedCodec
{
    private const int Missing = int.MinValue;

    private static readonly string[] ScripFields =
    [
        "ltp", "vol_traded_today", "last_traded_time", "exch_feed_time", "bid_size", "ask_size", "bid_price", "ask_price", "last_traded_qty", "tot_buy_qty", "tot_sell_qty",
        "avg_trade_price", "OI", "low_price", "high_price", "Yhigh", "Ylow", "lower_ckt", "upper_ckt", "open_price", "prev_close_price", "type", "symbol",
    ];

    private static readonly string[] IndexFields = ["ltp", "prev_close_price", "exch_feed_time", "high_price", "low_price", "open_price", "type", "symbol"];

    private static readonly string[] DepthFields =
    [
        .. Enumerable.Range(1, 5).Select(i => $"bid_price{i}"), .. Enumerable.Range(1, 5).Select(i => $"ask_price{i}"),
        .. Enumerable.Range(1, 5).Select(i => $"bid_size{i}"), .. Enumerable.Range(1, 5).Select(i => $"ask_size{i}"),
        .. Enumerable.Range(1, 5).Select(i => $"bid_order{i}"), .. Enumerable.Range(1, 5).Select(i => $"ask_order{i}"), "type", "symbol",
    ];

    private static readonly Dictionary<string, string> Segments = new()
    {
        ["1010"] = "nse_cm", ["1011"] = "nse_fo", ["1120"] = "mcx_fo", ["1210"] = "bse_cm", ["1211"] = "bse_fo", ["1212"] = "bcs_fo", ["1012"] = "cde_fo", ["1020"] = "nse_com",
    };

    private sealed record Active(string Exchange, string Symbol);

    private readonly Lock gate = new();
    private string? hsmKey;
    private bool authenticated;
    private readonly List<string> pendingHsm = [];
    private readonly Dictionary<string, string> symbolMappings = [];
    private readonly Dictionary<string, string> hsmToSymbol = [];
    private readonly Dictionary<string, Active> active = [];
    private readonly Dictionary<string, int> subscriptions = [];
    private readonly Dictionary<string, int> registry = [];
    private readonly HashSet<string> callbacks = [];
    private readonly Dictionary<int, string> topics = [];
    private readonly Dictionary<int, JsonObject> scrips = [];
    private readonly Dictionary<int, JsonObject> indices = [];
    private readonly Dictionary<int, JsonObject> depths = [];
    private readonly Dictionary<string, double> lastLtp = [];
    private List<string> outgoing = [];

    public string Broker => "fyers";

    public bool SendsBinary => true;

    public bool MessagesAreHex => true;

    // ---- the session ----------------------------------------------------------------------------------------

    public void Begin(BrokerSession session)
    {
        lock (gate)
        {
            hsmKey = ExtractHsmKey(session.AuthToken);
            authenticated = false;
        }
    }

    /// <summary>The key inside the access token's payload that signs the socket in; null when the token has none or has expired.</summary>
    public static string? ExtractHsmKey(string accessToken)
    {
        try
        {
            var token = accessToken.Contains(':', StringComparison.Ordinal) ? accessToken[(accessToken.IndexOf(':') + 1)..] : accessToken;
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject;
            var key = json?["hsm_key"] is JsonValue k && k.TryGetValue<string>(out var text) ? text : null;
            var expires = json?["exp"] is JsonValue e && e.TryGetValue<long>(out var seconds) ? seconds : 0;
            return expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds() < 0 ? null : key;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    // ---- what is sent ----------------------------------------------------------------------------------------

    private static string Hex(IEnumerable<byte> bytes) => Convert.ToHexStringLower(bytes.ToArray());

    private string? AuthMessage()
    {
        if (hsmKey is null) return null;
        const string source = "OpenAlgo-HSM";
        var key = Encoding.UTF8.GetBytes(hsmKey);
        var src = Encoding.UTF8.GetBytes(source);
        var size = 18 + key.Length + src.Length;
        var buffer = new List<byte>();
        buffer.AddRange(Be16(size - 2));
        buffer.Add(1);
        buffer.Add(4);
        buffer.Add(1);
        buffer.AddRange(Be16(key.Length));
        buffer.AddRange(key);
        buffer.Add(2);
        buffer.AddRange(Be16(1));
        buffer.Add((byte)'P');
        buffer.Add(3);
        buffer.AddRange(Be16(1));
        buffer.Add(1);
        buffer.Add(4);
        buffer.AddRange(Be16(src.Length));
        buffer.AddRange(src);
        return Hex(buffer);
    }

    private static byte[] Be16(int value) => [(byte)(value >> 8 & 0xFF), (byte)(value & 0xFF)];

    private static string SubscriptionMessage(IReadOnlyList<string> hsmSymbols)
    {
        var scrips = new List<byte>();
        scrips.AddRange(Be16(hsmSymbols.Count));
        foreach (var symbol in hsmSymbols)
        {
            var raw = Encoding.ASCII.GetBytes(symbol);
            scrips.Add((byte)raw.Length);
            scrips.AddRange(raw);
        }

        var message = new List<byte>();
        message.AddRange(Be16(6 + scrips.Count));
        message.Add(4);
        message.Add(2);
        message.Add(1);
        message.AddRange(Be16(scrips.Count));
        message.AddRange(scrips);
        message.Add(2);
        message.AddRange(Be16(1));
        message.Add(11);
        return Hex(message);
    }

    /// <summary>The feed name for an instrument: "if|segment|index name" for an index, else "dp|" or "sf|" with the part of the Fyers token after its first ten characters. Null when the token cannot give one.</summary>
    internal static string? HsmToken(string fytoken, string brokerSymbol, string? name, bool depth)
    {
        if (fytoken.Length < 10) return null;
        if (!Segments.TryGetValue(fytoken[..4], out var segment)) return null;
        if (brokerSymbol.EndsWith("-INDEX", StringComparison.Ordinal))
        {
            var indexName = name is { Length: > 0 } ? name : brokerSymbol.Split(':', 2)[1].Replace("-INDEX", "", StringComparison.Ordinal);
            return $"if|{segment}|{indexName}";
        }

        return $"{(depth ? "dp" : "sf")}|{segment}|{fytoken[10..]}";
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            // Queued by kind of data (quote or depth), the latest request for an instrument replacing the earlier one.
            var queue = new List<(string DataType, string Full, FeedRequest Request)>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3) continue;
                subscriptions[$"{r.Exchange}:{r.Symbol}:{mode}"] = mode;
                var dataType = mode == 3 ? "DepthUpdate" : "SymbolUpdate";
                var full = $"{r.Exchange}:{r.Symbol}";
                var at = queue.FindIndex(q => q.DataType == dataType && q.Full == full);
                if (at < 0) queue.Add((dataType, full, r));
                else queue[at] = (dataType, full, r);
            }

            var messages = new List<string>();
            foreach (var group in queue.GroupBy(q => q.DataType))
            {
                var hsmTokens = new List<string>();
                foreach (var (dataType, full, r) in group)
                {
                    registry[$"{dataType}_{full}"] = (int)r.Mode;
                    callbacks.Add($"{dataType}_{full}");
                    active[full] = new Active(r.Exchange, r.Symbol);
                    if (HsmToken(r.Token, r.BrokerSymbol ?? "", r.Name, dataType == "DepthUpdate") is not { } token) continue;
                    hsmTokens.Add(token);
                    symbolMappings[token] = r.BrokerSymbol ?? "";
                    hsmToSymbol[token] = full;
                }

                if (hsmTokens.Count == 0) continue;
                foreach (var token in hsmTokens) if (!pendingHsm.Contains(token)) pendingHsm.Add(token);
                // Until the sign-in is accepted the feed names wait, and go out the moment it is.
                if (authenticated) messages.Add(SubscriptionMessage(hsmTokens));
            }

            return messages;
        }
    }

    /// <summary>Fyers has no unsubscribe: the symbol just stops being published.</summary>
    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var modes = mode is { } named ? new List<int> { (int)named } : subscriptions.Where(s => s.Key.StartsWith($"{target.Exchange}:{target.Symbol}:", StringComparison.Ordinal)).Select(s => s.Value).ToList();
            foreach (var m in modes)
            {
                // A request that has been dropped is not published, whichever mode asked for it.
                subscriptions.Remove($"{target.Exchange}:{target.Symbol}:{m}");
            }

            return [];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            authenticated = false;
            return AuthMessage() is { } auth ? [auth] : [];
        }
    }

    public IReadOnlyList<string> TakeOutgoing()
    {
        lock (gate)
        {
            var taken = outgoing;
            outgoing = [];
            return taken;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    public void Reset()
    {
        lock (gate)
        {
            authenticated = false;
            pendingHsm.Clear();
            symbolMappings.Clear();
            hsmToSymbol.Clear();
            active.Clear();
            subscriptions.Clear();
            registry.Clear();
            callbacks.Clear();
            topics.Clear();
            scrips.Clear();
            indices.Clear();
            depths.Clear();
            lastLtp.Clear();
            outgoing = [];
        }
    }

    // ---- what comes back -------------------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        if (frame.Length < 3) return [];
        var published = new List<FeedPublish>();
        lock (gate)
        {
            switch (frame[2])
            {
                case 1:
                    if (!AuthAccepted(frame)) return [];
                    authenticated = true;
                    if (pendingHsm.Count > 0) outgoing.Add(SubscriptionMessage(pendingHsm));
                    break;
                case 6:
                    DataFeed(frame, nowMs, published);
                    break;
            }
        }

        return published;
    }

    /// <summary>The first field of the sign-in answer is a status text, "K" when the key was accepted.</summary>
    private static bool AuthAccepted(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[5..]);
        return 7 + length <= data.Length && Encoding.UTF8.GetString(data.Slice(7, length)) == "K";
    }

    private void DataFeed(ReadOnlySpan<byte> data, long nowMs, List<FeedPublish> published)
    {
        if (data.Length < 9) return;
        var count = BinaryPrimitives.ReadUInt16BigEndian(data[7..]);
        var offset = 9;
        for (var i = 0; i < count; i++)
        {
            if (offset >= data.Length) break;
            var type = data[offset++];
            if (type == 83) offset = Snapshot(data, offset, nowMs, published);
            else if (type == 85) offset = Update(data, offset, nowMs, published);
            else break;
        }
    }

    private int Snapshot(ReadOnlySpan<byte> data, int offset, long nowMs, List<FeedPublish> published)
    {
        if (offset + 3 > data.Length) return offset;
        var topicId = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        var nameLength = data[offset++];
        if (offset + nameLength > data.Length) return offset;
        var name = Encoding.UTF8.GetString(data.Slice(offset, nameLength));
        offset += nameLength;
        topics[topicId] = name;

        if (name.StartsWith("sf|", StringComparison.Ordinal)) return Block(data, offset, "sf", ScripFields, true, name, topicId, scrips, nowMs, published);
        if (name.StartsWith("if|", StringComparison.Ordinal)) return Block(data, offset, "if", IndexFields, false, name, topicId, indices, nowMs, published);
        if (name.StartsWith("dp|", StringComparison.Ordinal)) return Block(data, offset, "dp", DepthFields, true, name, topicId, depths, nowMs, published);
        return offset;
    }

    /// <summary>One snapshot: its integer fields by the table for its kind (a missing value is left out), then for scrips and depth a multiplier, a precision and three strings.</summary>
    private int Block(ReadOnlySpan<byte> data, int offset, string kind, string[] fields, bool hasTail, string topicName, int topicId, Dictionary<int, JsonObject> store, long nowMs, List<FeedPublish> published)
    {
        if (offset + 1 > data.Length) return offset;
        var count = data[offset++];
        var state = new JsonObject { ["type"] = kind };
        for (var index = 0; index < count; index++)
        {
            if (offset + 4 > data.Length) break;
            var value = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            offset += 4;
            if (value != Missing && index < fields.Length) state[fields[index]] = value;
        }

        if (hasTail)
        {
            offset += 2;
            if (offset + 3 > data.Length) return offset;
            state["multiplier"] = (int)BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            state["precision"] = (int)data[offset++];
            foreach (var field in new[] { "exchange", "exchange_token", "symbol" })
            {
                if (offset + 1 > data.Length) break;
                var length = data[offset++];
                if (offset + length > data.Length) break;
                state[field] = Encoding.UTF8.GetString(data.Slice(offset, length));
                offset += length;
            }
        }

        if (symbolMappings.TryGetValue(topicName, out var original)) state["original_symbol"] = original;
        state["hsm_token"] = topicName;
        store[topicId] = state;
        Dispatch((JsonObject)state.DeepClone(), nowMs, published);
        return offset;
    }

    /// <summary>An update changes some fields of the stored state; the whole state goes out once, if anything changed.</summary>
    private int Update(ReadOnlySpan<byte> data, int offset, long nowMs, List<FeedPublish> published)
    {
        if (offset + 3 > data.Length) return offset;
        var topicId = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        int count = data[offset++];
        var topic = topics.GetValueOrDefault(topicId, "");
        (Dictionary<int, JsonObject>? store, string[] fields) = topic.StartsWith("sf|", StringComparison.Ordinal) ? (scrips, ScripFields)
            : topic.StartsWith("if|", StringComparison.Ordinal) ? (indices, IndexFields)
            : topic.StartsWith("dp|", StringComparison.Ordinal) ? (depths, DepthFields)
            : (null, []);
        var state = store is not null && store.TryGetValue(topicId, out var s) ? s : null;
        if (state is null) return offset + count * 4;

        var changed = false;
        for (var index = 0; index < count; index++)
        {
            if (offset + 4 > data.Length) break;
            var value = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            offset += 4;
            if (value == Missing || index >= fields.Length) continue;
            if (state[fields[index]] is JsonValue current && current.TryGetValue<int>(out var old) && old == value) continue;
            state[fields[index]] = value;
            changed = true;
        }

        if (changed)
        {
            var copy = (JsonObject)state.DeepClone();
            copy["update_type"] = "live";
            Dispatch(copy, nowMs, published);
        }

        return offset;
    }

    // ---- from a feed's state to a published tick -------------------------------------------------------------

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>The divisor and number of decimals that turn a feed's integer price into rupees: ten to the precision times the multiplier (a multiplier of zero counts as one).</summary>
    private static (double Divisor, int Precision) Scale(JsonObject data)
    {
        var precision = data["precision"] is null ? 2 : Int(data["precision"]);
        var multiplier = data["multiplier"] is null ? 1 : Int(data["multiplier"]);
        if (multiplier <= 0) multiplier = 1;
        return (Math.Pow(10, precision) * multiplier, precision);
    }

    private static (string Exchange, string Name) SplitSymbol(JsonObject data)
    {
        var symbol = Str(data["original_symbol"]) is { Length: > 0 } original ? original : Str(data["symbol"]);
        if (!symbol.Contains(':', StringComparison.Ordinal)) return (Str(data["exchange"]), symbol);
        var parts = symbol.Split(':', 2);
        var name = parts[1].Contains('-', StringComparison.Ordinal) ? parts[1].Split('-')[0] : parts[1];
        return (parts[0], name);
    }

    private static JsonNode Price(JsonObject data, string field)
    {
        var (divisor, precision) = Scale(data);
        var value = Int(data[field]);
        return value == 0 ? 0.0 : BookFormatting.RoundPy(value / divisor, precision);
    }

    private static JsonObject Quote(JsonObject data, long nowMs)
    {
        var (exchange, name) = SplitSymbol(data);
        var (_, precision) = Scale(data);
        double Num(string field) => Price(data, field).GetValue<double>();
        var ltp = Num("ltp");
        var previous = Num("prev_close_price");
        double change = 0, changePercent = 0;
        if (ltp != 0 && previous != 0)
        {
            change = BookFormatting.RoundPy(ltp - previous, precision);
            changePercent = BookFormatting.RoundPy(change / previous * 100, 2);
        }

        return new JsonObject
        {
            ["symbol"] = $"{exchange}:{name}", ["exchange"] = exchange, ["token"] = Str(data["exchange_token"]), ["ltp"] = ltp,
            ["open"] = Num("open_price"), ["high"] = Num("high_price"), ["low"] = Num("low_price"), ["close"] = previous,
            ["bid_price"] = Num("bid_price"), ["ask_price"] = Num("ask_price"), ["bid_size"] = Int(data["bid_size"]), ["ask_size"] = Int(data["ask_size"]),
            ["volume"] = Int(data["vol_traded_today"]), ["oi"] = Int(data["OI"]), ["upper_circuit"] = Num("upper_ckt"), ["lower_circuit"] = Num("lower_ckt"),
            ["last_traded_time"] = Int(data["last_traded_time"]), ["exchange_time"] = Int(data["exch_feed_time"]), ["avg_trade_price"] = Num("avg_trade_price"),
            ["last_trade_quantity"] = Int(data["last_traded_qty"]), ["total_buy_quantity"] = Int(data["tot_buy_qty"]), ["total_sell_quantity"] = Int(data["tot_sell_qty"]),
            ["change"] = change, ["change_percent"] = changePercent, ["timestamp"] = nowMs / 1000, ["data_type"] = "Quote",
        };
    }

    private static JsonObject? DepthTick(JsonObject data, long nowMs)
    {
        var (exchange, name) = SplitSymbol(data);
        var buy = new JsonArray();
        var sell = new JsonArray();
        for (var i = 1; i <= 5; i++)
        {
            var bid = Price(data, $"bid_price{i}").GetValue<double>();
            var ask = Price(data, $"ask_price{i}").GetValue<double>();
            if (bid > 0) buy.Add(new JsonObject { ["price"] = bid, ["quantity"] = Int(data[$"bid_size{i}"]), ["orders"] = Int(data[$"bid_order{i}"]) });
            if (ask > 0) sell.Add(new JsonObject { ["price"] = ask, ["quantity"] = Int(data[$"ask_size{i}"]), ["orders"] = Int(data[$"ask_order{i}"]) });
        }

        return new JsonObject
        {
            ["symbol"] = $"{exchange}:{name}", ["exchange"] = exchange, ["token"] = Str(data["exchange_token"]), ["depth"] = new JsonObject { ["buy"] = buy, ["sell"] = sell },
            ["timestamp"] = nowMs / 1000, ["data_type"] = "Depth",
        };
    }

    /// <summary>An index has no book, so its depth is empty levels around the index value.</summary>
    private static JsonObject? IndexDepth(JsonObject data, long nowMs)
    {
        if (Int(data["ltp"]) == 0) return null;
        var (exchange, name) = SplitSymbol(data);
        return new JsonObject
        {
            ["symbol"] = $"{exchange}:{name}", ["exchange"] = exchange, ["token"] = Str(data["exchange_token"]), ["ltp"] = Price(data, "ltp").GetValue<double>(),
            ["depth"] = new JsonObject { ["buy"] = new JsonArray(), ["sell"] = new JsonArray() }, ["timestamp"] = nowMs / 1000, ["data_type"] = "Depth",
        };
    }

    private void Dispatch(JsonObject data, long nowMs, List<FeedPublish> published)
    {
        var type = Str(data["type"]) is { Length: > 0 } t ? t : "sf";
        var updateType = Str(data["update_type"]) is { Length: > 0 } u ? u : "snapshot";
        var mapped = Quote(data, nowMs);
        if (Str(mapped["symbol"]).Length == 0) return;

        // The feed name says which subscription this is; a feed nobody subscribed is not guessed at.
        if (Str(data["hsm_token"]) is not { Length: > 0 } hsm || !hsmToSymbol.TryGetValue(hsm, out var full) || !active.TryGetValue(full, out var sub)) return;

        var dispatches = new List<(string DataType, string Kind)>();
        if (type == "dp")
        {
            if (callbacks.Contains($"DepthUpdate_{full}")) dispatches.Add(("DepthUpdate", "Depth"));
        }
        else if (type == "if")
        {
            if (callbacks.Contains($"SymbolUpdate_{full}")) dispatches.Add(("SymbolUpdate", "Quote"));
            if (callbacks.Contains($"DepthUpdate_{full}")) dispatches.Add(("DepthUpdate", "Depth"));
        }
        else if (callbacks.Contains($"SymbolUpdate_{full}"))
        {
            dispatches.Add(("SymbolUpdate", "Quote"));
        }

        if (type is "sf" or "if" && mapped["ltp"]!.GetValue<double>() != 0) lastLtp[full] = mapped["ltp"]!.GetValue<double>();
        foreach (var (dataType, kind) in dispatches)
        {
            JsonObject? side = kind == "Depth" ? (type == "dp" ? DepthTick(data, nowMs) : IndexDepth(data, nowMs)) : (JsonObject)mapped.DeepClone();
            if (side is null) continue;
            if (type == "dp")
            {
                if (lastLtp.TryGetValue(full, out var ltp)) side["ltp"] = ltp;
            }

            side["symbol"] = sub.Symbol;
            side["exchange"] = sub.Exchange;
            side["update_type"] = updateType;
            side["timestamp"] = nowMs / 1000;

            // The mode a tick is published under is the latest request for that kind of data; a request that has since been dropped publishes nothing.
            if (!registry.TryGetValue($"{dataType}_{full}", out var mode) || !subscriptions.ContainsKey($"{sub.Exchange}:{sub.Symbol}:{mode}")) continue;
            side["subscription_mode"] = mode;
            published.Add(new FeedPublish($"{sub.Exchange}_{sub.Symbol}_{(mode == 1 ? "LTP" : mode == 3 ? "DEPTH" : "QUOTE")}", side));
        }
    }
}
