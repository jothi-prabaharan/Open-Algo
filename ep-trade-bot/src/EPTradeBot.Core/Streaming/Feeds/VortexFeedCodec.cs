using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Rupeezy's Vortex price feed (broker/rupeezy/streaming/rupeezy_websocket.py and rupeezy_adapter.py). Subscribing is one JSON text frame per instrument with a
/// mode (ltp, ohlcv or full); an instrument has ONE mode at Vortex, the richest any client asked for, and its packet is fanned out to every mode it can serve.
/// Price frames are little-endian binary: a packet count, then each packet with its length; the length says the mode (22 ltp, 62 ohlcv, 266 full).
/// </summary>
public sealed class VortexFeedCodec : IFeedCodec
{
    public const int MaxInstruments = 1000;
    private const int LtpSize = 22, OhlcvSize = 62, FullSize = 266;

    private sealed record Entry(string Symbol, string Exchange, string Ticker, HashSet<StreamMode> Modes);

    private readonly Lock gate = new();
    private readonly Dictionary<(string Exchange, long Token), Entry> instruments = [];
    private readonly Dictionary<string, string> wanted = [];

    public string Broker => "rupeezy";

    private static string VortexMode(StreamMode mode) => mode switch { StreamMode.Ltp => "ltp", StreamMode.Quote => "ohlcv", _ => "full" };

    private static string Richest(IEnumerable<StreamMode> modes) => VortexMode(modes.Max());

    public static string Ticker(string brokerExchange, string brokerSymbol) => $"{(brokerExchange.Length > 3 ? brokerExchange[..3] : brokerExchange)}:{brokerSymbol}";

    private static string Frame(string ticker, string mode, string type) =>
        new JsonObject { ["ticker"] = ticker, ["mode"] = mode, ["message_type"] = type }.ToJsonString();

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        var messages = new List<string>();
        lock (gate)
        {
            foreach (var r in requests)
            {
                if (!long.TryParse(r.Token, out var token) || r.BrokerSymbol is null) continue;
                var key = (r.BrokerExchange, token);
                var ticker = Ticker(r.BrokerExchange, r.BrokerSymbol);
                var isNew = !instruments.ContainsKey(key);
                if (isNew) instruments[key] = new Entry(r.Symbol, r.Exchange, ticker, []);
                var entry = instruments[key];
                var modeWasNew = !entry.Modes.Contains(r.Mode);
                entry.Modes.Add(r.Mode);

                // Vortex takes at most 1000 instruments on a connection: one more is refused, leaving nothing behind.
                if (!wanted.ContainsKey(ticker) && wanted.Count >= MaxInstruments)
                {
                    if (isNew) instruments.Remove(key);
                    else if (modeWasNew) entry.Modes.Remove(r.Mode);
                    continue;
                }

                var mode = Richest(entry.Modes);
                wanted[ticker] = mode;
                messages.Add(Frame(ticker, mode, "subscribe"));
            }
        }

        return messages;
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var key = instruments.Keys.FirstOrDefault(k => k.Exchange == target.BrokerExchange && long.TryParse(target.Token, out var t) && t == k.Token);
            if (key == default || !instruments.TryGetValue(key, out var entry)) return [];
            if (mode is { } one) entry.Modes.Remove(one);
            else entry.Modes.Clear();
            if (entry.Modes.Count > 0)
            {
                var richest = Richest(entry.Modes);
                wanted[entry.Ticker] = richest;
                return [Frame(entry.Ticker, richest, "subscribe")];
            }

            instruments.Remove(key);
            return wanted.Remove(entry.Ticker, out var stored) ? [Frame(entry.Ticker, stored, "unsubscribe")] : [];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return wanted.Select(w => Frame(w.Key, w.Value, "subscribe")).ToList();
    }

    public void Reset()
    {
        lock (gate)
        {
            instruments.Clear();
            wanted.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- binary frames ----------------------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var published = new List<FeedPublish>();
        if (frame.Length < 2) return published;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            if (offset + 2 > frame.Length) break;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
            offset += 2;
            var available = Math.Min(length, frame.Length - offset);
            if (Parse(frame.Slice(offset, available)) is { } tick) Publish(tick, published);
            offset += length;
        }

        return published;
    }

    private sealed record Tick(string Mode, string Exchange, long Token, double Ltp, long Ltt, double Open, double High, double Low, double Close, long Volume,
        long LastUpdate, long Ltq, double Average, long BuyQuantity, long SellQuantity, long OpenInterest, List<JsonObject>? Buy, List<JsonObject>? Sell, double Upper, double Lower);

    private static string Exchange(ReadOnlySpan<byte> raw) => Encoding.ASCII.GetString(raw.TrimEnd((byte)0));

    private static Tick? Parse(ReadOnlySpan<byte> raw)
    {
        var p = raw.ToArray();
        if (p.Length is not (LtpSize or OhlcvSize or FullSize)) return null;
        double D(int at) => BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(at));
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(at));
        var exchange = Exchange(p.AsSpan(0, 10));
        var token = (long)I(10);
        if (p.Length == LtpSize) return new Tick("ltp", exchange, token, D(14), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, 0, 0);
        if (p.Length == OhlcvSize) return new Tick("ohlcv", exchange, token, D(14), I(22), D(26), D(34), D(42), D(50), I(58), 0, 0, 0, 0, 0, 0, null, null, 0, 0);

        List<JsonObject> Side(int start) => Enumerable.Range(0, 5).Select(i =>
        {
            var at = start + (i * 16);
            return new JsonObject { ["price"] = D(at), ["quantity"] = I(at + 8), ["orders"] = I(at + 12) };
        }).ToList();

        return new Tick("full", exchange, token, D(14), I(22), D(26), D(34), D(42), D(50), I(58), I(62), I(66), D(70),
            BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(78)), BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(86)), I(94), Side(98), Side(178), I(258) / 100.0, I(262) / 100.0);
    }

    // ---- from a packet to what clients receive ----------------------------------------------------------------

    private static int Rank(string topic) => topic switch { "LTP" => 1, "QUOTE" => 2, _ => 3 };

    private static string Topic(StreamMode mode) => mode switch { StreamMode.Ltp => "LTP", StreamMode.Quote => "QUOTE", _ => "DEPTH" };

    private void Publish(Tick tick, List<FeedPublish> published)
    {
        Entry entry;
        string[] topics;
        lock (gate)
        {
            if (!instruments.TryGetValue((tick.Exchange, tick.Token), out var known)) return;
            entry = known;
            var packetTopic = tick.Mode switch { "ltp" => "LTP", "ohlcv" => "QUOTE", _ => "DEPTH" };
            // A packet carries everything a poorer mode needs, so it is published to each mode subscribed up to its own richness.
            topics = entry.Modes.Select(Topic).Where(t => Rank(t) <= Rank(packetTopic)).Distinct().ToArray();
            if (topics.Length == 0) topics = [packetTopic];
        }

        foreach (var topic in topics)
        {
            var mode = topic switch { "LTP" => StreamMode.Ltp, "QUOTE" => StreamMode.Quote, _ => StreamMode.Depth };
            published.Add(new FeedPublish(MarketTopics.Format(entry.Exchange, entry.Symbol, mode), Normalize(tick, entry, topic)));
        }
    }

    /// <summary>Python's round(x, 2): the exact binary value rounded half to even, which Math.Round does not do for a value such as 2.675.</summary>
    public static double Round2(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
        var negative = x < 0;
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(x));
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent++; else mantissa |= 1L << 52;
        exponent -= 1075;
        BigInteger scaled = new BigInteger(mantissa) * 100;
        BigInteger quotient;
        if (exponent >= 0) quotient = scaled << exponent;
        else
        {
            var divisor = BigInteger.One << -exponent;
            quotient = BigInteger.DivRem(scaled, divisor, out var remainder);
            var twice = remainder * 2;
            var compare = twice.CompareTo(divisor);
            if (compare > 0 || (compare == 0 && !quotient.IsEven)) quotient += 1;
        }

        var result = (double)quotient / 100.0;
        return negative ? -result : result;
    }

    private static JsonObject Normalize(Tick tick, Entry entry, string topic)
    {
        long? Seconds(long v) => v != 0 ? v * 1000 : null;
        var timestamp = (tick.LastUpdate != 0 ? tick.LastUpdate : tick.Ltt) * 1000;
        var data = new JsonObject
        {
            ["symbol"] = entry.Symbol, ["exchange"] = entry.Exchange, ["token"] = tick.Token.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ltp"] = tick.Ltp, ["last_price"] = tick.Ltp, ["ltt"] = Seconds(tick.Ltt), ["timestamp"] = timestamp != 0 ? timestamp : null,
        };
        if (tick.Mode != "ltp" && tick.Close != 0)
        {
            var change = tick.Ltp - tick.Close;
            data["close"] = tick.Close;
            data["prev_close"] = tick.Close;
            data["change"] = Round2(change);
            data["change_percent"] = Round2(change / tick.Close * 100);
        }

        if (topic is "QUOTE" or "DEPTH")
        {
            data["open"] = tick.Open;
            data["high"] = tick.High;
            data["low"] = tick.Low;
            data["volume"] = tick.Volume;
        }

        if (topic == "DEPTH")
        {
            data["last_quantity"] = tick.Ltq;
            data["average_price"] = tick.Average;
            data["total_buy_quantity"] = tick.BuyQuantity;
            data["total_sell_quantity"] = tick.SellQuantity;
            data["oi"] = tick.OpenInterest;
            data["open_interest"] = tick.OpenInterest;
            data["depth"] = new JsonObject
            {
                ["buy"] = new JsonArray((tick.Buy ?? []).Select(l => (JsonNode?)l.DeepClone()).ToArray()),
                ["sell"] = new JsonArray((tick.Sell ?? []).Select(l => (JsonNode?)l.DeepClone()).ToArray()),
            };
            data["upper_circuit"] = tick.Upper;
            data["lower_circuit"] = tick.Lower;
        }

        return data;
    }
}
