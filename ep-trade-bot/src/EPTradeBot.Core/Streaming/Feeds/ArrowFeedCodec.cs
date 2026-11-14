using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Arrow's market-data stream (broker/arrow/streaming). A subscribe is a JSON frame whose array key is the mode (ltpc, quote or full), at most 100 tokens a frame; the stream sends one
/// big-endian packet per message, told apart by length (13 ltp, 17 ltpc, 93 quote, 249 or 241 full). Arrow holds one mode per token, so a token wanted in two modes is asked for in the
/// last, and a tick is published to the topic of its own mode only; an unsubscribe ends the token whatever mode is named.
/// </summary>
public sealed class ArrowFeedCodec : IFeedCodec
{
    private const int MaxInstruments = 1000;
    private const int MaxPerFrame = 100;

    private readonly Lock gate = new();
    private readonly HashSet<long> subscribed = [];
    private readonly Dictionary<long, string> modeMap = [];
    private readonly Dictionary<long, (string Symbol, string Exchange)> info = [];

    public string Broker => "arrow";

    private static string ArrowMode(StreamMode mode) => mode switch { StreamMode.Ltp => "ltpc", StreamMode.Quote => "quote", StreamMode.Depth => "full", _ => "quote" };

    private static string Frame(string code, string mode, IEnumerable<long> tokens) =>
        new JsonObject { ["code"] = code, ["mode"] = mode, [mode] = new JsonArray(tokens.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) }.ToJsonString();

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            // The limit counts what has been sent, not what is queued in this call.
            var sent = subscribed.Count;
            List<(long Token, string Mode)> queue = [];
            foreach (var r in requests)
            {
                if (!long.TryParse(r.Token, out var token)) continue;
                info[token] = (r.Symbol, r.Exchange);
                if (sent + 1 > MaxInstruments) continue;
                queue.Add((token, ArrowMode(r.Mode)));
            }

            var frames = new List<string>();
            var i = 0;
            while (i < queue.Count)
            {
                var mode = queue[i].Mode;
                var tokens = new List<long>();
                while (i < queue.Count && queue[i].Mode == mode && tokens.Count < MaxPerFrame) tokens.Add(queue[i++].Token);
                frames.Add(Frame("sub", mode, tokens));
                foreach (var t in tokens)
                {
                    modeMap[t] = mode;
                    subscribed.Add(t);
                }
            }

            return frames;
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            if (!long.TryParse(target.Token, out var token)) return [];
            var current = modeMap.GetValueOrDefault(token, "quote");
            subscribed.Remove(token);
            modeMap.Remove(token);
            info.Remove(token);
            return [Frame("unsub", current, [token])];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            foreach (var group in subscribed.GroupBy(t => modeMap.GetValueOrDefault(t, "quote")))
            {
                foreach (var chunk in group.Order().Chunk(MaxPerFrame)) frames.Add(Frame("sub", group.Key, chunk));
            }

            return frames;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            subscribed.Clear();
            modeMap.Clear();
            info.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- packets ----------------------------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> raw, long nowMs)
    {
        var frame = raw.ToArray();
        var n = frame.Length;
        if (n < 13) return [];
        var mode = n switch { 13 => "ltp", 17 => "ltpc", 93 => "quote", 249 or 241 => "full", _ => n >= 241 ? "full" : n >= 93 ? "quote" : "ltp" };
        uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(at));
        ulong U64(int at) => BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(at));

        var token = (long)U32(0);
        var ltp = U32(4) / 100.0;
        var tick = new JsonObject { ["mode"] = mode, ["ltp"] = ltp, ["timestamp"] = nowMs };
        if (n < 93)
        {
            if (n == 17) tick["close"] = U32(13) / 100.0;
        }
        else
        {
            var feedTime = U32(65);
            tick["timestamp"] = feedTime != 0 ? (long)feedTime * 1000 : nowMs;
            tick["ltq"] = (long)U32(13);
            tick["average_price"] = U32(17) / 100.0;
            tick["total_buy_quantity"] = (long)U64(21);
            tick["total_sell_quantity"] = (long)U64(29);
            tick["open"] = U32(37) / 100.0;
            tick["high"] = U32(41) / 100.0;
            tick["close"] = U32(45) / 100.0;
            tick["low"] = U32(49) / 100.0;
            tick["volume"] = (long)U64(53);
            tick["oi"] = (long)U64(69);
            if (U32(61) != 0) tick["ltt"] = (long)U32(61) * 1000;
            if (n >= 241)
            {
                tick["lower_limit"] = U32(93) / 100.0;
                tick["upper_limit"] = U32(97) / 100.0;
                var offset = n >= 249 ? 109 : 101;
                if (n >= offset + 140)
                {
                    var buy = new JsonArray();
                    var sell = new JsonArray();
                    for (var i = 0; i < 10; i++)
                    {
                        var at = offset + (i * 14);
                        var level = new JsonObject { ["quantity"] = (long)U64(at), ["price"] = U32(at + 8) / 100.0, ["orders"] = (int)BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(at + 12)) };
                        (i < 5 ? buy : sell).Add(level);
                    }

                    tick["depth"] = new JsonObject { ["buy"] = buy, ["sell"] = sell };
                }
            }
        }

        (string Symbol, string Exchange) known;
        lock (gate)
        {
            if (!info.TryGetValue(token, out known)) return [];
        }

        var topic = mode switch { "ltp" or "ltpc" => StreamMode.Ltp, "quote" => StreamMode.Quote, _ => StreamMode.Depth };
        return [new FeedPublish(MarketTopics.Format(known.Exchange, known.Symbol, topic), Normalize(tick, token, known.Symbol, known.Exchange, topic))];
    }

    private static JsonObject Normalize(JsonObject tick, long token, string symbol, string exchange, StreamMode topic)
    {
        var ltp = tick["ltp"]!.GetValue<double>();
        var data = new JsonObject
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["token"] = token.ToString(System.Globalization.CultureInfo.InvariantCulture), ["ltp"] = ltp, ["last_price"] = ltp,
            ["ltt"] = tick["ltt"]?.DeepClone() ?? tick["timestamp"]!.DeepClone(), ["timestamp"] = tick["timestamp"]!.DeepClone(),
        };
        if (tick["close"] is { } close)
        {
            var c = close.GetValue<double>();
            data["close"] = c;
            data["prev_close"] = c;
            if (c != 0)
            {
                var change = ltp - c;
                data["change"] = VortexFeedCodec.Round2(change);
                data["change_percent"] = VortexFeedCodec.Round2(change / c * 100);
            }
        }

        if (topic is StreamMode.Quote or StreamMode.Depth)
        {
            data["volume"] = tick["volume"]?.DeepClone() ?? 0;
            data["last_quantity"] = tick["ltq"]?.DeepClone() ?? 0;
            data["average_price"] = tick["average_price"]?.DeepClone() ?? 0;
            data["total_buy_quantity"] = tick["total_buy_quantity"]?.DeepClone() ?? 0;
            data["total_sell_quantity"] = tick["total_sell_quantity"]?.DeepClone() ?? 0;
            foreach (var key in new[] { "open", "high", "low" }) if (tick[key] is { } v) data[key] = v.DeepClone();
            if (tick["oi"] is { } oi)
            {
                data["oi"] = oi.DeepClone();
                data["open_interest"] = oi.DeepClone();
            }
        }

        if (topic == StreamMode.Depth)
        {
            if (tick["depth"] is { } depth) data["depth"] = depth.DeepClone();
            if (tick["upper_limit"] is { } upper)
            {
                data["upper_circuit"] = upper.DeepClone();
                data["lower_circuit"] = tick["lower_limit"]!.DeepClone();
            }
        }

        return data;
    }
}
