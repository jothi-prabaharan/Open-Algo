using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// IIFL Capital's market-data stream (broker/iiflcapital/streaming): MQTT topics prod/marketfeed/mw/v1/{segment}/{token} (market), .../index/v1/... (indexes) and .../oi/v1/... (open interest, futures
/// and options in quote or depth mode only), a hundred topics to a subscribe. One 186-byte little-endian packet per update carries the price, day range, totals and five levels a side; the open-interest
/// packet (four 32-bit integers) is kept and merged into the next market tick. A stream is subscribed once per instrument at the highest mode asked for, and the packet is cut into the LTP, quote and
/// depth messages for the modes held.
/// </summary>
public sealed class IiflCapitalFeedCodec : IMqttFeedCodec
{
    private const string Market = "prod/marketfeed/mw/v1/", Index = "prod/marketfeed/index/v1/", Oi = "prod/marketfeed/oi/v1/";
    private const int PerCall = 100;
    private const int MaxInstruments = 5800;
    private const int PacketSize = 186;
    private static readonly HashSet<string> OiExchanges = ["NFO", "BFO", "CDS", "BCD", "MCX"];
    private static readonly HashSet<string> IndexExchanges = ["NSE_INDEX", "BSE_INDEX", "MCX_INDEX", "GLOBAL_INDEX"];

    private sealed class Held
    {
        public required string Exchange { get; init; }
        public required string Symbol { get; init; }
        public required string Segment { get; init; }
        public required string Token { get; init; }
        public SortedSet<int> Modes { get; } = [];
    }

    private sealed record Stream(bool IsIndex, bool Oi, string Segment, string Token);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Held> held = [];
    private readonly Dictionary<string, (string Symbol, string Exchange)> keyToSymbol = [];
    private readonly OrderedDictionary<string, Stream> streams = [];
    private readonly Dictionary<string, (long Oi, long High, long Low, long Previous)> oiCache = [];

    public string Broker => "iiflcapital";

    private static IReadOnlyList<MqttCommand> Batches(IEnumerable<string> topics) => topics.Chunk(PerCall).Select(c => new MqttCommand(true, c)).ToList();

    public IReadOnlyList<MqttCommand> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var pending = new List<string>();
            foreach (var r in requests)
            {
                var token = (r.Token ?? "").Trim();
                var segment = (r.BrokerExchange ?? "").Trim();
                if (token.Length == 0 || segment.Length == 0) continue;
                var isIndex = IndexExchanges.Contains(r.Exchange);
                var suffix = $"{segment.ToLowerInvariant()}/{token}";
                var key = $"{r.Exchange}:{r.Symbol}";
                if (!held.TryGetValue(key, out var h)) held[key] = h = new Held { Exchange = r.Exchange, Symbol = r.Symbol, Segment = segment, Token = token };
                h.Modes.Add((int)r.Mode);
                keyToSymbol[suffix] = (r.Symbol, r.Exchange);

                var effective = h.Modes.Max;
                var includeOi = effective != 1 && OiExchanges.Contains(r.Exchange) && !isIndex;

                // The cap counts only instruments new to the connection; a repeat takes no further place.
                if (!streams.ContainsKey(suffix) && streams.Count + 1 > MaxInstruments) continue;
                streams[suffix] = new Stream(isIndex, includeOi, segment.ToLowerInvariant(), token);
                pending.Add((isIndex ? Index : Market) + suffix);
                if (includeOi && !isIndex) pending.Add(Oi + suffix);
            }

            return Batches(pending);
        }
    }

    public IReadOnlyList<MqttCommand> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var key = $"{target.Exchange}:{target.Symbol}";
            if (!held.TryGetValue(key, out var h)) return [];
            if (mode is { } m && h.Modes.Any(x => x != (int)m))
            {
                h.Modes.Remove((int)m);
                return [];
            }

            held.Remove(key);
            var suffix = $"{h.Segment.ToLowerInvariant()}/{h.Token}";
            keyToSymbol.Remove(suffix);
            oiCache.Remove(suffix);
            if (!streams.Remove(suffix, out var s)) return [];
            var topics = new List<string> { (s.IsIndex ? Index : Market) + suffix };
            if (s.Oi) topics.Add(Oi + suffix);
            return [new MqttCommand(false, topics)];
        }
    }

    public IReadOnlyList<MqttCommand> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var topics = new List<string>();
            foreach (var (suffix, s) in streams)
            {
                topics.Add((s.IsIndex ? Index : Market) + suffix);
                if (s.Oi) topics.Add(Oi + suffix);
            }

            return Batches(topics);
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            held.Clear();
            keyToSymbol.Clear();
            streams.Clear();
            oiCache.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeMessage(string topic, ReadOnlySpan<byte> payload, long nowMs)
    {
        var data = payload.ToArray();
        lock (gate)
        {
            if (topic.StartsWith(Market, StringComparison.Ordinal)) return Feed(topic[Market.Length..], data, nowMs);
            if (topic.StartsWith(Index, StringComparison.Ordinal)) return Feed(topic[Index.Length..], data, nowMs);
            if (topic.StartsWith(Oi, StringComparison.Ordinal)) Interest(topic[Oi.Length..], data);
            return [];
        }
    }

    private void Interest(string key, byte[] payload)
    {
        if (payload.Length < 16) return;
        oiCache[key] = (BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(12)));
    }

    private static JsonArray Levels(byte[] p, int first, double inv)
    {
        var array = new JsonArray();
        for (var i = first; i < first + 5; i++)
        {
            var at = 66 + (i * 12);
            array.Add(new JsonObject
            {
                ["quantity"] = (long)BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(at)), ["price"] = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(at + 4)) * inv, ["orders"] = (long)BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(at + 8)),
            });
        }

        return array;
    }

    private List<FeedPublish> Feed(string key, byte[] p, long nowMs)
    {
        if (!streams.TryGetValue(key, out var stream)) return [];
        if (p.Length < PacketSize) return [];

        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(at));
        long U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(at));
        var divisor = I32(58);
        if (divisor == 0) divisor = 100;
        var inv = 1.0 / divisor;

        var ltp = I32(0) * inv;
        var ltt = I32(62);
        var suffix = $"{stream.Segment}/{stream.Token}";
        if (!keyToSymbol.TryGetValue(suffix, out var named)) return [];
        if (!held.TryGetValue($"{named.Exchange}:{named.Symbol}", out var h)) return [];
        var modes = h.Modes;

        var baseData = () => new JsonObject { ["symbol"] = named.Symbol, ["exchange"] = named.Exchange, ["ltp"] = ltp, ["ltt"] = ltt != 0 ? ltt : 0, ["timestamp"] = nowMs };
        var output = new List<FeedPublish>();
        if (modes.Contains(1))
        {
            var d = baseData();
            d["mode"] = "ltp";
            output.Add(new FeedPublish(MarketTopics.Format(named.Exchange, named.Symbol, StreamMode.Ltp), d));
        }

        if (modes.Count == 1 && modes.Contains(1)) return output;
        var quote = baseData();
        quote["open"] = I32(20) * inv;
        quote["high"] = I32(12) * inv;
        quote["low"] = I32(16) * inv;
        quote["close"] = I32(24) * inv;
        quote["volume"] = U32(8);
        quote["last_quantity"] = U32(4);
        quote["average_price"] = I32(28) * inv;
        quote["total_buy_quantity"] = U32(50);
        quote["total_sell_quantity"] = U32(54);
        quote["bid"] = I32(38) * inv;
        quote["ask"] = I32(46) * inv;
        quote["bid_quantity"] = U32(34);
        quote["ask_quantity"] = U32(42);
        if (modes.Contains(2) || modes.Contains(3))
        {
            if (stream.Oi && oiCache.TryGetValue(key, out var cached))
            {
                quote["oi"] = cached.Oi;
                quote["open_interest"] = cached.Oi;
            }
        }

        if (modes.Contains(2))
        {
            var d = (JsonObject)quote.DeepClone();
            d["mode"] = "quote";
            output.Add(new FeedPublish(MarketTopics.Format(named.Exchange, named.Symbol, StreamMode.Quote), d));
        }

        if (modes.Contains(3))
        {
            var d = (JsonObject)quote.DeepClone();
            d["mode"] = "full";
            d["depth"] = new JsonObject { ["buy"] = Levels(p, 0, inv), ["sell"] = Levels(p, 5, inv) };
            output.Add(new FeedPublish(MarketTopics.Format(named.Exchange, named.Symbol, StreamMode.Depth), d));
        }

        return output;
    }
}
