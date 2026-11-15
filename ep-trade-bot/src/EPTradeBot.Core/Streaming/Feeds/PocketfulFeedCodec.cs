using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Pocketful's market data stream (broker/pocketful/streaming). A subscribe is a JSON frame {a: subscribe, m: marketdata | compact_marketdata | full_snapquote, v: [[exchange code, token], ...]} with no limit on a frame; a burst goes grouped by stream and
/// exchange code, and a request is sent again when it is made again. LTP is the compact stream, Quote the detailed one and Depth the snapquote. A packet is read by its own mode (see <see cref="PocketfulPackets"/>) and belongs to the first subscription, in the order
/// they were made, that holds its token and exchange code, whatever mode that subscription asked for; it is published to the topic of that subscription's mode, with the fields of the packet's own kind.
/// </summary>
public sealed class PocketfulFeedCodec : IFeedCodec
{
    private sealed record Sub(string Symbol, string Exchange, string Token, int Code, int Mode, int Stream);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];

    public string Broker => "pocketful";

    private static int ExchangeCode(string brokerExchange) => brokerExchange switch { "NSE" => 1, "NFO" => 2, "CDS" => 3, "MCX" => 4, "BSE" => 6, "BFO" => 7, "NSE_INDEX" => 1, "BSE_INDEX" => 6, _ => 1 };

    private static int StreamOf(int mode) => mode switch { 1 => 2, 2 => 1, _ => 4 };

    private static string StreamName(int stream) => stream switch { 1 => "marketdata", 2 => "compact_marketdata", _ => "full_snapquote" };

    private static string Frame(string action, int stream, IEnumerable<(int Code, long Token)> instruments) =>
        new JsonObject { ["a"] = action, ["v"] = new JsonArray(instruments.Select(i => (JsonNode?)new JsonArray(i.Code, i.Token)).ToArray()), ["m"] = StreamName(stream) }.ToJsonString();

    private static string KeyOf(string symbol, string exchange, int mode) => mode == 3 ? $"{symbol}_{exchange}_{mode}_5" : $"{symbol}_{exchange}_{mode}";

    private static List<string> Group(IEnumerable<Sub> items)
    {
        var groups = new OrderedDictionary<(int Stream, int Code), OrderedDictionary<string, long>>();
        foreach (var s in items)
        {
            if (!long.TryParse(s.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token)) continue;
            if (!groups.TryGetValue((s.Stream, s.Code), out var instruments)) groups[(s.Stream, s.Code)] = instruments = [];
            instruments[s.Token] = token;
        }

        return groups.Select(g => Frame("subscribe", g.Key.Stream, g.Value.Values.Select(t => (g.Key.Code, t)))).ToList();
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queued = new OrderedDictionary<string, Sub>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3 || string.IsNullOrEmpty(r.Token)) continue;
                var sub = new Sub(r.Symbol, r.Exchange, r.Token, ExchangeCode(r.BrokerExchange), mode, StreamOf(mode));
                var key = KeyOf(r.Symbol, r.Exchange, mode);
                subs[key] = sub;
                queued[key] = sub;
            }

            return Group(queued.Values);
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            foreach (var m in mode is { } given ? [(int)given] : new[] { 1, 2, 3 })
            {
                var prefix = $"{target.Symbol}_{target.Exchange}_{m}";
                var key = subs.Keys.FirstOrDefault(k => (k == prefix || k.StartsWith(prefix + "_", StringComparison.Ordinal)) && subs[k].Symbol == target.Symbol && subs[k].Exchange == target.Exchange && subs[k].Mode == m);
                if (key is null || !subs.Remove(key, out var removed)) continue;
                if (long.TryParse(removed.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token)) frames.Add(Frame("unsubscribe", removed.Stream, [(removed.Code, token)]));
            }

            return frames;
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return Group(subs.Values);
    }

    public void Reset()
    {
        lock (gate) subs.Clear();
    }

    // ---- packets ----------------------------------------------------------------------------------------------

    private static bool Is(JsonNode? mode, int value) => mode is JsonValue v && v.TryGetValue<double>(out var d) ? d == value : mode is JsonValue w && w.TryGetValue<long>(out var l) && l == value;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : node is JsonValue s && s.TryGetValue<string>(out var str) ? str
        : node is JsonValue e && e.TryGetValue<System.Text.Json.JsonElement>(out var el) ? el.ValueKind switch { System.Text.Json.JsonValueKind.Number => el.GetRawText(), System.Text.Json.JsonValueKind.String => el.GetString(), _ => null } : null;

    /// <summary>Python's <c>res.get(key, 0) / 100</c>: a missing key is nought, a number is in paise, anything else is an error that drops the packet.</summary>
    private static double Div(JsonObject packet, string key)
    {
        if (!packet.TryGetPropertyValue(key, out var v)) return 0;
        return Number(v) / 100;
    }

    private static double Number(JsonNode? v) => v is JsonValue value && value.TryGetValue<double>(out var d) ? d : v is JsonValue l && l.TryGetValue<long>(out var n) ? n
        : v is JsonValue e && e.TryGetValue<System.Text.Json.JsonElement>(out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number ? el.GetDouble() : throw new FormatException("not a number");

    private static JsonNode? Raw(JsonObject packet, string key) => packet.TryGetPropertyValue(key, out var v) ? v?.DeepClone() : JsonValue.Create(0);

    private IReadOnlyList<FeedPublish> Publish(JsonObject packet, int kind, long nowMs)
    {
        if (packet.Count == 0) return [];
        try
        {
            var token = Text(packet["instrument_token"]);
            var code = packet["exchange_code"];
            Sub? sub;
            lock (gate)
                sub = subs.Values.FirstOrDefault(s => s.Token == token && code is JsonValue c && (c.TryGetValue<long>(out var n) ? n == s.Code : c.TryGetValue<double>(out var d) && d == s.Code));
            if (sub is null) return [];
            var topic = $"{sub.Exchange}_{sub.Symbol}_{sub.Mode switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" }}";
            var data = new JsonObject();
            switch (kind)
            {
                case PocketfulPackets.Detailed:
                    data["ltp"] = Div(packet, "last_traded_price"); data["ltt"] = Raw(packet, "last_traded_time"); data["volume"] = Raw(packet, "trade_volume");
                    data["open"] = Div(packet, "open_price"); data["high"] = Div(packet, "high_price"); data["low"] = Div(packet, "low_price"); data["close"] = Div(packet, "close_price");
                    data["last_trade_quantity"] = Raw(packet, "last_traded_quantity"); data["average_price"] = Div(packet, "average_trade_price");
                    data["total_buy_quantity"] = Raw(packet, "total_buy_quantity"); data["total_sell_quantity"] = Raw(packet, "total_sell_quantity"); data["oi"] = Raw(packet, "currentOpenInterest");
                    break;
                case PocketfulPackets.Compact:
                    data["ltp"] = Div(packet, "last_traded_price"); data["ltt"] = Raw(packet, "last_traded_time"); data["change"] = Div(packet, "change"); data["oi"] = Raw(packet, "currentOpenInterest");
                    data["bid_price"] = Div(packet, "bidPrice"); data["ask_price"] = Div(packet, "askPrice");
                    break;
                default:
                    data["ltp"] = Div(packet, "averageTradePrice"); data["open"] = Div(packet, "open"); data["high"] = Div(packet, "high"); data["low"] = Div(packet, "low"); data["close"] = Div(packet, "close");
                    data["volume"] = Raw(packet, "volume"); data["total_buy_quantity"] = Raw(packet, "totalBuyQty"); data["total_sell_quantity"] = Raw(packet, "totalSellQty");
                    break;
            }

            data["symbol"] = sub.Symbol; data["exchange"] = sub.Exchange; data["mode"] = sub.Mode; data["timestamp"] = nowMs;
            if (kind == PocketfulPackets.Snapquote && packet.ContainsKey("bidPrices") && packet.ContainsKey("askPrices"))
                data["depth"] = new JsonObject { ["buy"] = Side(packet, "bidPrices", "bidQtys", "buyers"), ["sell"] = Side(packet, "askPrices", "askQtys", "sellers") };
            return [new FeedPublish(topic, data)];
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException or InvalidCastException)
        {
            // A value that is not a number: OpenAlgo's handler fails and the packet is lost.
            return [];
        }
    }

    private static JsonArray Side(JsonObject packet, string prices, string quantities, string orders)
    {
        var rows = new JsonArray();
        var p = packet[prices] as JsonArray ?? [];
        var q = packet[quantities] as JsonArray ?? [];
        var o = packet[orders] as JsonArray ?? [];
        for (var i = 0; i < Math.Min(5, p.Count); i++)
            rows.Add(new JsonObject { ["price"] = Number(p[i]) / 100, ["quantity"] = i < q.Count ? q[i]?.DeepClone() : 0, ["orders"] = i < o.Count ? o[i]?.DeepClone() : 0 });
        return rows;
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var bytes = frame.ToArray();
        return PocketfulPackets.ModeOf(bytes) switch
        {
            PocketfulPackets.Detailed => Publish(PocketfulPackets.DecodeDetailed(bytes), PocketfulPackets.Detailed, nowMs),
            PocketfulPackets.Compact => Publish(PocketfulPackets.DecodeCompact(bytes), PocketfulPackets.Compact, nowMs),
            PocketfulPackets.Snapquote => Publish(PocketfulPackets.DecodeSnapquote(bytes), PocketfulPackets.Snapquote, nowMs),
            _ => [],
        };
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs)
    {
        var mode = PocketfulPackets.ModeOfText(frame);
        if (Is(mode, PocketfulPackets.Compact)) return Publish(PocketfulPackets.DecodeCompact(frame), PocketfulPackets.Compact, nowMs);
        if (Is(mode, PocketfulPackets.Snapquote)) return Publish(PocketfulPackets.DecodeSnapquote(frame), PocketfulPackets.Snapquote, nowMs);
        return [];
    }
}
