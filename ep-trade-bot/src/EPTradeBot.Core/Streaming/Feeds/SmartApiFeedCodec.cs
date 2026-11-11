using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Angel One's SmartAPI stream v2 (broker/angel/streaming/smartWebSocketV2.py and angel_adapter.py). One subscribe message per (mode, exchange type)
/// in a batch; frames are little-endian binary with prices in paise.
/// </summary>
public sealed class SmartApiFeedCodec : IFeedCodec
{
    private static readonly Dictionary<string, int> ExchangeTypes = new(StringComparer.Ordinal)
    {
        ["NSE"] = 1, ["NFO"] = 2, ["BSE"] = 3, ["BFO"] = 4, ["MCX"] = 5, ["NCX"] = 7, ["CDS"] = 13, ["NSE_INDEX"] = 1, ["BSE_INDEX"] = 3,
    };

    private sealed record Entry(string Symbol, string Exchange, string BrokerExchange, string Token, int Mode, int ExchangeType);

    private readonly Lock gate = new();

    // Insertion order matters (a Python dict): the first stays first when replaced, and a removal leaves the rest in order.
    private readonly List<(string Key, Entry Entry)> subscriptions = [];
    private readonly Dictionary<(string Token, int ExchangeType), Entry> tokenIndex = [];

    public string Broker => "angel";

    /// <summary>The exchange type SmartAPI means, from the broker's exchange code. An unknown one falls back to NSE cash, as OpenAlgo does.</summary>
    public static int ExchangeType(string brokerExchange) => ExchangeTypes.GetValueOrDefault(brokerExchange, 1);

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        List<Entry> queued = [];
        lock (gate)
        {
            foreach (var r in requests)
            {
                var entry = new Entry(r.Symbol, r.Exchange, r.BrokerExchange, r.Token, (int)r.Mode, ExchangeType(r.BrokerExchange));
                var key = $"{r.Symbol}_{r.Exchange}_{(int)r.Mode}";
                var at = subscriptions.FindIndex(s => s.Key == key);
                if (at >= 0) subscriptions[at] = (key, entry);
                else subscriptions.Add((key, entry));
                tokenIndex[(r.Token, entry.ExchangeType)] = entry;
                queued.Add(entry);
            }
        }

        return Messages(queued, "batch", nowMs);
    }

    /// <summary>One message per mode, with one group per exchange type, in the order they first appear.</summary>
    private static List<string> Messages(IEnumerable<Entry> entries, string prefix, long nowMs)
    {
        var byMode = new List<(int Mode, List<(int ExchangeType, List<string> Tokens)> Groups)>();
        foreach (var e in entries)
        {
            var mode = byMode.Find(m => m.Mode == e.Mode);
            if (mode.Groups is null)
            {
                mode = (e.Mode, []);
                byMode.Add(mode);
            }

            var group = mode.Groups.Find(g => g.ExchangeType == e.ExchangeType);
            if (group.Tokens is null)
            {
                group = (e.ExchangeType, []);
                mode.Groups.Add(group);
            }

            group.Tokens.Add(e.Token);
        }

        return byMode.Select(m => new JsonObject
        {
            ["correlationID"] = $"{prefix}_{nowMs}_{m.Mode}",
            ["action"] = 1,
            ["params"] = new JsonObject
            {
                ["mode"] = m.Mode,
                ["tokenList"] = new JsonArray(m.Groups.Select(g => (JsonNode?)new JsonObject
                {
                    ["exchangeType"] = g.ExchangeType, ["tokens"] = new JsonArray(g.Tokens.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                }).ToArray()),
            },
        }.ToJsonString()).ToList();
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        var exchangeType = ExchangeType(target.BrokerExchange);
        List<int> modes;
        lock (gate) modes = mode is { } m ? [(int)m] : subscriptions.Where(s => s.Entry.Exchange == target.Exchange && s.Entry.Symbol == target.Symbol).Select(s => s.Entry.Mode).ToList();

        var messages = new List<string>();
        foreach (var each in modes)
        {
            lock (gate)
            {
                subscriptions.RemoveAll(s => s.Key == $"{target.Symbol}_{target.Exchange}_{each}");
                // The tick index points at a surviving subscription of the token, or goes.
                var remaining = subscriptions.Select(s => s.Entry).FirstOrDefault(e => e.Token == target.Token && e.ExchangeType == exchangeType);
                if (remaining is not null) tokenIndex[(target.Token, exchangeType)] = remaining;
                else tokenIndex.Remove((target.Token, exchangeType));
            }

            messages.Add(new JsonObject
            {
                ["correlationID"] = $"{target.Symbol}_{target.Exchange}_{each}",
                ["action"] = 0,
                ["params"] = new JsonObject
                {
                    ["mode"] = each,
                    ["tokenList"] = new JsonArray(new JsonObject { ["exchangeType"] = exchangeType, ["tokens"] = new JsonArray(JsonValue.Create(target.Token)) }),
                },
            }.ToJsonString());
        }

        return messages;
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        List<Entry> all;
        lock (gate) all = subscriptions.Select(s => s.Entry).ToList();
        return Messages(all, "resub", nowMs);
    }

    public void Reset()
    {
        lock (gate)
        {
            subscriptions.Clear();
            tokenIndex.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- binary frames ----------------------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        // A frame too short to hold the header (a heartbeat) carries no data.
        if (frame.Length < 51) return [];
        var data = frame.ToArray();
        var mode = data[0];
        var exchangeType = data[1];
        var token = Token(data.AsSpan(2, 25));
        if (mode is not (1 or 2 or 3)) return [];

        Entry? entry;
        lock (gate)
        {
            if (!tokenIndex.TryGetValue((token, exchangeType), out entry))
                entry = subscriptions.Select(s => s.Entry).FirstOrDefault(e => e.Token == token && e.ExchangeType == exchangeType);
        }

        if (entry is null) return [];
        JsonObject? body;
        try
        {
            body = Normalize(data, mode);
        }
        catch (ArgumentOutOfRangeException)
        {
            return [];
        }

        // The mode in the data is the subscription's, not the frame's, as OpenAlgo publishes it.
        body["symbol"] = entry.Symbol;
        body["exchange"] = entry.Exchange;
        body["mode"] = entry.Mode;
        body["timestamp"] = nowMs;
        return [new FeedPublish(MarketTopics.Format(entry.Exchange, entry.Symbol, (StreamMode)mode), body)];
    }

    private static string Token(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static long Long(byte[] d, int at) => BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(at));

    private static double Double(byte[] d, int at) => BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(at));

    private static JsonObject Normalize(byte[] d, int mode)
    {
        var ltp = Long(d, 43) / 100.0;
        var ltt = Long(d, 35);
        if (mode == 1) return new JsonObject { ["ltp"] = ltp, ["ltt"] = ltt };

        // Quote and snap quote carry the fields up to byte 123.
        if (d.Length < 123) throw new ArgumentOutOfRangeException(nameof(d));
        var volume = Long(d, 67);
        var open = Long(d, 91) / 100.0;
        var high = Long(d, 99) / 100.0;
        var low = Long(d, 107) / 100.0;
        var close = Long(d, 115) / 100.0;
        if (mode == 2)
        {
            return new JsonObject
            {
                ["ltp"] = ltp, ["ltt"] = ltt, ["volume"] = volume, ["open"] = open, ["high"] = high, ["low"] = low, ["close"] = close,
                ["last_trade_quantity"] = Long(d, 51), ["average_price"] = Long(d, 59) / 100.0,
                ["total_buy_quantity"] = Double(d, 75), ["total_sell_quantity"] = Double(d, 83),
            };
        }

        if (d.Length < 379) throw new ArgumentOutOfRangeException(nameof(d));
        var result = new JsonObject
        {
            ["ltp"] = ltp, ["ltt"] = ltt, ["volume"] = volume,
            ["open"] = open, ["high"] = high, ["low"] = low, ["close"] = close,
            ["last_quantity"] = Long(d, 51), ["oi"] = Long(d, 131),
            ["upper_circuit"] = Long(d, 347) / 100.0, ["lower_circuit"] = Long(d, 355) / 100.0,
        };

        // Levels flagged 0 and the rest, in the packet's order. The stream names them the other way round from the book, and OpenAlgo follows
        // the stream: the "buy" side is the levels with a non-zero flag.
        List<JsonObject> flagZero = [], flagged = [];
        for (var at = 147; at + 20 <= 347; at += 20)
        {
            var level = new JsonObject
            {
                ["price"] = Long(d, at + 10) > 0 ? Long(d, at + 10) / 100.0 : (double)Long(d, at + 10),
                ["quantity"] = Long(d, at + 2),
                ["orders"] = (int)BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 18)),
            };
            (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)) == 0 ? flagZero : flagged).Add(level);
        }

        JsonArray Side(List<JsonObject> levels)
        {
            if (levels.Count == 0) return new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 }).ToArray());
            return new JsonArray(levels.Select(l => (JsonNode?)l.DeepClone()).ToArray());
        }

        result["depth"] = new JsonObject { ["buy"] = Side(flagged), ["sell"] = Side(flagZero) };
        return result;
    }
}
