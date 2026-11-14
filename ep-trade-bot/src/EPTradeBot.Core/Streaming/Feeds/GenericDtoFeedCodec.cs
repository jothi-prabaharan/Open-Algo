using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// The InvestRight market-data stream of HDFC Securities (broker/hdfcsecurities/streaming), whose GenericDTO schema HDFC Sky ships byte for byte. A subscribe is one JSON frame
/// (scripId and type LTP or ALL, a hundred scrips a frame), an unsubscribe is the same frame under "unSubscribe" (capital S). The stream sends protobuf: a list of packets, or a bare
/// packet. The packet type names the exchange (a token is only unique inside one), and the circuit and open-interest packets are partial refreshes merged into the last full quote.
/// </summary>
public sealed class GenericDtoFeedCodec(string broker, Func<string, string> prefixOf) : IFeedCodec
{
    private const int MaxInstruments = 1500;
    private const int MaxPerFrame = 100;

    private sealed class Info
    {
        public required string Symbol { get; init; }
        public required string Exchange { get; init; }
        public required SortedSet<int> Modes { get; init; }
    }

    private readonly Lock gate = new();
    private readonly Dictionary<(string Exchange, long Token), Info> info = [];
    private readonly Dictionary<(string Exchange, long Token), JsonObject> snapshots = [];

    /// <summary>What is wanted, in the order it was asked for (the repeat after a reconnect walks it).</summary>
    private readonly OrderedDictionary<string, string> desired = [];

    /// <summary>What was last sent for a scrip.</summary>
    private readonly Dictionary<string, string> sent = [];

    private readonly List<(string Scrip, string Type)> pending = [];

    public string Broker => broker;

    private static string TypeOf(int mode) => mode == 1 ? "LTP" : "ALL";

    private static string Scrip(string prefix, long token) => $"{prefix}_{token}";

    private static string Message(string key, IEnumerable<(string Scrip, string Type)> items) =>
        new JsonObject
        {
            ["heart_beat"] = false,
            [key] = new JsonArray(items.Select(i => (JsonNode?)new JsonObject { ["scripId"] = i.Scrip, ["type"] = i.Type }).ToArray()),
        }.ToJsonString();

    /// <summary>Queues a scrip unless the connection already holds the most it may; a refused one is simply not sent.</summary>
    private void Queue(string scrip, string type)
    {
        if (sent.Count + 1 > MaxInstruments) return;
        desired[scrip] = type;
        pending.Add((scrip, type));
    }

    /// <summary>Sends everything queued: grouped by type, a hundred to a frame, leaving out anything unsubscribed or asked for at another type since.</summary>
    private List<string> Drain()
    {
        var frames = new List<string>();
        var byType = new List<(string Type, List<string> Scrips)>();
        foreach (var (scrip, type) in pending)
        {
            var group = byType.Find(g => g.Type == type);
            if (group.Scrips is null)
            {
                group = (type, []);
                byType.Add(group);
            }

            group.Scrips.Add(scrip);
        }

        pending.Clear();
        foreach (var (type, scrips) in byType)
        {
            foreach (var chunk in scrips.Chunk(MaxPerFrame))
            {
                var live = chunk.Where(s => desired.TryGetValue(s, out var t) && t == type).ToList();
                if (live.Count == 0) continue;
                frames.Add(Message("subscribe", live.Select(s => (s, type))));
                foreach (var s in live) sent[s] = type;
            }
        }

        return frames;
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            foreach (var r in requests)
            {
                if (!long.TryParse(r.Token, out var token)) continue;
                var key = (r.Exchange, token);
                var modes = info.TryGetValue(key, out var existing) ? new SortedSet<int>(existing.Modes) : [];
                modes.Add((int)r.Mode);
                info[key] = new Info { Symbol = r.Symbol, Exchange = r.Exchange, Modes = modes };
                Queue(Scrip(prefixOf(r.Exchange), token), TypeOf(modes.Max));
            }

            return Drain();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            if (!long.TryParse(target.Token, out var token)) return [];
            var key = (target.Exchange, token);
            var scrip = Scrip(prefixOf(target.Exchange), token);
            SortedSet<int> left = [];
            if (info.TryGetValue(key, out var held) && mode is { } m)
            {
                left = new SortedSet<int>(held.Modes);
                left.Remove((int)m);
            }

            if (left.Count > 0)
            {
                info[key] = new Info { Symbol = target.Symbol, Exchange = target.Exchange, Modes = left };
                Queue(scrip, TypeOf(left.Max));
                return Drain();
            }

            info.Remove(key);
            snapshots.Remove(key);

            // Everything about the scrip is dropped in one step, then the broker is told with the type it last had.
            var type = sent.GetValueOrDefault(scrip) ?? desired.GetValueOrDefault(scrip) ?? "ALL";
            desired.Remove(scrip);
            sent.Remove(scrip);
            pending.RemoveAll(p => p.Scrip == scrip);
            return [Message("unSubscribe", [(scrip, type)])];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            foreach (var group in desired.GroupBy(d => d.Value))
            {
                foreach (var chunk in group.Select(d => d.Key).Chunk(MaxPerFrame))
                {
                    frames.Add(Message("subscribe", chunk.Select(s => (s, group.Key))));
                    foreach (var s in chunk) sent[s] = group.Key;
                }
            }

            return frames;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            info.Clear();
            snapshots.Clear();
            desired.Clear();
            sent.Clear();
            pending.Clear();
        }
    }

    /// <summary>Text frames are acknowledgements and errors; neither carries a tick.</summary>
    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- packets ----------------------------------------------------------------------------------------------

    private const int Heartbeat = 16;

    private static readonly Dictionary<long, string> ExchangeOf = new()
    {
        [0] = "NSE", [10] = "NSE", [1] = "CDS", [11] = "CDS", [12] = "CDS", [3] = "NFO", [13] = "NFO", [14] = "NFO", [17] = "NFO",
        [4] = "BSE", [6] = "BFO", [15] = "BFO", [18] = "BFO", [7] = "MCX", [2] = "NSE_INDEX", [5] = "BSE_INDEX",
    };

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> raw, long nowMs)
    {
        var payload = raw.ToArray();
        var packets = GenericDto.ParseList(payload);
        if (packets is null || packets.Count == 0)
        {
            var single = GenericDto.Parse(payload);
            packets = single is null ? [] : [single];
        }

        var published = new List<FeedPublish>();
        foreach (var packet in packets)
        {
            var tick = ToTick(packet, nowMs);
            if (tick is null) continue;
            Publish(tick, published);
        }

        return published;
    }

    /// <summary>The index fields of a tick (kind, price, day range, close, a zero volume and the packet's own time when it has one).</summary>
    internal static void FillIndex(JsonObject tick, GenericDto p)
    {
        var ix = p.Index ?? new GenericDto.IndexData();
        tick["kind"] = "index";
        tick["ltp"] = ix.Value;
        tick["open"] = ix.Open;
        tick["high"] = ix.High;
        tick["low"] = ix.Low;
        tick["close"] = ix.Close;
        tick["volume"] = 0;
        if (ix.Timestamp != 0) tick["timestamp"] = ix.Timestamp;
    }

    /// <summary>The market-by-price fields of a tick (price, day range, volume, totals, open interest, circuit band, trade time and the book, five levels a side).</summary>
    internal static void FillMbp(JsonObject tick, GenericDto p)
    {
        var m = p.Mbp ?? new GenericDto.MbpData();
        tick["kind"] = "mbp";
        tick["ltp"] = m.Ltp;
        tick["open"] = m.Open;
        tick["high"] = m.High;
        tick["low"] = m.Low;
        tick["close"] = m.Close;
        tick["volume"] = m.Volume;
        tick["ltq"] = m.Ltq;
        tick["average_price"] = m.Average;
        tick["total_buy_quantity"] = m.TotalBuy;
        tick["total_sell_quantity"] = m.TotalSell;
        tick["oi"] = m.Oi;
        tick["lower_limit"] = m.Lower;
        tick["upper_limit"] = m.Upper;
        if (m.LastTradeTime != 0) tick["ltt"] = m.LastTradeTime;
        var buy = new JsonArray();
        var sell = new JsonArray();
        foreach (var l in m.Levels)
        {
            var entry = new JsonObject { ["price"] = l.Price, ["quantity"] = l.Quantity, ["orders"] = l.Orders };
            (l.Buy ? buy : sell).Add(entry);
        }

        if (buy.Count > 0 || sell.Count > 0)
        {
            tick["depth"] = new JsonObject
            {
                ["buy"] = new JsonArray(buy.Take(5).Select(x => x!.DeepClone()).ToArray()),
                ["sell"] = new JsonArray(sell.Take(5).Select(x => x!.DeepClone()).ToArray()),
            };
        }
    }

    private static JsonObject? ToTick(GenericDto p, long nowMs)
    {
        if (p.PacketType == Heartbeat) return null;
        if (p.InstrumentId == 0) return null;
        var tick = new JsonObject
        {
            ["token"] = p.InstrumentId,
            ["timestamp"] = p.PacketTimestamp != 0 ? p.PacketTimestamp : nowMs,
            ["exchange"] = ExchangeOf.GetValueOrDefault(p.PacketType),
        };

        switch (p.PacketType)
        {
            case 2 or 5:
                FillIndex(tick, p);
                return tick;
            case 17 or 18:
                return null; // option greeks are not part of the tick contract
            case 10 or 11 or 13:
                tick["kind"] = "circuit";
                tick["lower_limit"] = (p.Mbp ?? new GenericDto.MbpData()).Lower;
                tick["upper_limit"] = (p.Mbp ?? new GenericDto.MbpData()).Upper;
                return tick;
            case 12 or 14 or 15:
                tick["kind"] = "oi";
                tick["oi"] = (p.Mbp ?? new GenericDto.MbpData()).Oi;
                return tick;
        }

        if (p.PacketType is 0 or 1 or 3 or 4 or 6 or 7 || p.Mbp is not null)
        {
            FillMbp(tick, p);
            return tick;
        }

        return null;
    }

    private void Publish(JsonObject tick, List<FeedPublish> output)
    {
        lock (gate)
        {
            var token = tick["token"]!.GetValue<long>();
            (string Exchange, long Token)? key = null;
            if (tick["exchange"] is JsonValue ex)
            {
                var candidate = (ex.GetValue<string>(), token);
                if (info.ContainsKey(candidate)) key = candidate;
            }
            else
            {
                var matches = info.Keys.Where(k => k.Token == token).ToList();
                if (matches.Count == 1) key = matches[0];
            }

            if (key is not { } k2) return;
            var held = info[k2];
            var resolved = Merge(k2, tick);
            if (resolved is null) return;
            var topics = held.Modes.Reverse().Select(m => (StreamMode)m).ToList();
            foreach (var mode in topics) output.Add(new FeedPublish(MarketTopics.Format(held.Exchange, held.Symbol, mode), Normalize(resolved, token, held.Symbol, held.Exchange, mode)));
        }
    }

    /// <summary>A circuit or open-interest packet carries no price: it is folded into the last full quote, and publishes nothing when there is none.</summary>
    private JsonObject? Merge((string Exchange, long Token) key, JsonObject tick)
    {
        var kind = tick["kind"]!.GetValue<string>();
        var fields = kind switch { "circuit" => new[] { "lower_limit", "upper_limit" }, "oi" => new[] { "oi" }, _ => null };
        if (fields is null)
        {
            snapshots[key] = tick;
            return tick;
        }

        if (!snapshots.TryGetValue(key, out var snapshot)) return null;
        var merged = (JsonObject)snapshot.DeepClone();
        foreach (var f in fields) if (tick[f] is { } v) merged[f] = v.DeepClone();
        merged["timestamp"] = tick["timestamp"]!.DeepClone();
        snapshots[key] = merged;
        return merged;
    }

    internal static JsonObject Normalize(JsonObject tick, long token, string symbol, string exchange, StreamMode topic)
    {
        var ltp = tick["ltp"]?.GetValue<double>() ?? 0;
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

/// <summary>The GenericDTO messages, read by hand. A field of the wrong wire type is an unknown field and is skipped, as protobuf does; a cut-off or malformed message fails the whole parse.</summary>
internal sealed class GenericDto
{
    public sealed class IndexData
    {
        public double Value, High, Low, Open, Close;
        public long Timestamp;
    }

    public sealed class Level
    {
        public long Quantity, Orders;
        public double Price;
        public bool Buy;
    }

    public sealed class MbpData
    {
        public double Ltp, Open, High, Close, Low, Average, Lower, Upper;
        public long LastTradeTime, Volume, Ltq, TotalBuy, TotalSell, Oi;
        public List<Level> Levels = [];
    }

    public long InstrumentId, PacketTimestamp, PacketType;
    public MbpData? Mbp;
    public IndexData? Index;

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        public int At;

        public readonly bool Done => At >= data.Length;

        public bool Varint(out ulong value)
        {
            value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (At >= data.Length) return false;
                var b = data[At++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }

            return false;
        }

        public bool Fixed64(out double value)
        {
            value = 0;
            if (At + 8 > data.Length) return false;
            value = BitConverter.ToDouble(data.Slice(At, 8));
            At += 8;
            return true;
        }

        public bool Bytes(out ReadOnlySpan<byte> value)
        {
            value = default;
            if (!Varint(out var len) || len > (ulong)(data.Length - At)) return false;
            value = data.Slice(At, (int)len);
            At += (int)len;
            return true;
        }

        /// <summary>Steps over a field of a number or wire type this reader has no use for; false when it runs off the end or is a wire type protobuf does not have here.</summary>
        public bool Skip(int wire)
        {
            switch (wire)
            {
                case 0: return Varint(out _);
                case 1:
                    if (At + 8 > data.Length) return false;
                    At += 8;
                    return true;
                case 2: return Bytes(out _);
                case 5:
                    if (At + 4 > data.Length) return false;
                    At += 4;
                    return true;
                default: return false;
            }
        }

        public bool Tag(out int field, out int wire)
        {
            field = wire = 0;
            if (!Varint(out var tag)) return false;
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return field != 0;
        }
    }

    /// <summary>A list of packets (field 1, length-delimited); null when the bytes are not a well-formed message.</summary>
    public static List<GenericDto>? ParseList(ReadOnlySpan<byte> bytes)
    {
        var list = new List<GenericDto>();
        var r = new Reader(bytes);
        while (!r.Done)
        {
            if (!r.Tag(out var field, out var wire)) return null;
            if (field == 1 && wire == 2)
            {
                if (!r.Bytes(out var inner)) return null;
                var packet = Parse(inner);
                if (packet is null) return null;
                list.Add(packet);
            }
            else if (!r.Skip(wire)) return null;
        }

        return list;
    }

    public static GenericDto? Parse(ReadOnlySpan<byte> bytes)
    {
        var p = new GenericDto();
        var r = new Reader(bytes);
        while (!r.Done)
        {
            if (!r.Tag(out var field, out var wire)) return null;
            switch (field, wire)
            {
                case (1, 0):
                    if (!r.Varint(out var id)) return null;
                    p.InstrumentId = (long)id;
                    break;
                case (2, 2):
                    if (!r.Bytes(out var mb)) return null;
                    p.Mbp ??= new MbpData();
                    if (!ParseMbp(mb, p.Mbp)) return null;
                    break;
                case (3, 2):
                    if (!r.Bytes(out var ib)) return null;
                    p.Index ??= new IndexData();
                    if (!ParseIndex(ib, p.Index)) return null;
                    break;
                case (8, 0):
                    if (!r.Varint(out var ts)) return null;
                    p.PacketTimestamp = (long)ts;
                    break;
                case (9, 0):
                    if (!r.Varint(out var pt)) return null;
                    p.PacketType = (int)(long)pt;
                    break;
                case (10, 2):
                    if (!r.Bytes(out var gb) || !Scan(gb)) return null;
                    break;
                default:
                    if (!r.Skip(wire)) return null;
                    break;
            }
        }

        return p;
    }

    /// <summary>Checks that a message is well formed without keeping anything of it (the option greeks).</summary>
    private static bool Scan(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes);
        while (!r.Done)
        {
            if (!r.Tag(out _, out var wire) || !r.Skip(wire)) return false;
        }

        return true;
    }

    private static bool ParseIndex(ReadOnlySpan<byte> bytes, IndexData ix)
    {
        var r = new Reader(bytes);
        while (!r.Done)
        {
            if (!r.Tag(out var field, out var wire)) return false;
            switch (field, wire)
            {
                case (2, 1): if (!r.Fixed64(out ix.Value)) return false; break;
                case (3, 1): if (!r.Fixed64(out ix.High)) return false; break;
                case (4, 1): if (!r.Fixed64(out ix.Low)) return false; break;
                case (5, 1): if (!r.Fixed64(out ix.Open)) return false; break;
                case (6, 1): if (!r.Fixed64(out ix.Close)) return false; break;
                case (10, 0):
                    if (!r.Varint(out var ts)) return false;
                    ix.Timestamp = (long)ts;
                    break;
                default:
                    if (!r.Skip(wire)) return false;
                    break;
            }
        }

        return true;
    }

    private static bool ParseMbp(ReadOnlySpan<byte> bytes, MbpData m)
    {
        var r = new Reader(bytes);
        while (!r.Done)
        {
            if (!r.Tag(out var field, out var wire)) return false;
            ulong v;
            switch (field, wire)
            {
                case (1, 1): if (!r.Fixed64(out m.Ltp)) return false; break;
                case (2, 0): if (!r.Varint(out v)) return false; m.LastTradeTime = (long)v; break;
                case (5, 1): if (!r.Fixed64(out m.Open)) return false; break;
                case (6, 1): if (!r.Fixed64(out m.High)) return false; break;
                case (7, 1): if (!r.Fixed64(out m.Close)) return false; break;
                case (8, 1): if (!r.Fixed64(out m.Low)) return false; break;
                case (9, 0): if (!r.Varint(out v)) return false; m.Volume = (long)v; break;
                case (10, 0): if (!r.Varint(out v)) return false; m.Ltq = (long)v; break;
                case (11, 1): if (!r.Fixed64(out m.Average)) return false; break;
                case (12, 2):
                    if (!r.Bytes(out var list) || !ParseLevels(list, m.Levels)) return false;
                    break;
                case (13, 0): if (!r.Varint(out v)) return false; m.TotalBuy = (long)v; break;
                case (14, 0): if (!r.Varint(out v)) return false; m.TotalSell = (long)v; break;
                case (15, 1): if (!r.Fixed64(out m.Lower)) return false; break;
                case (16, 1): if (!r.Fixed64(out m.Upper)) return false; break;
                case (17, 0): if (!r.Varint(out v)) return false; m.Oi = (long)v; break;
                default:
                    if (!r.Skip(wire)) return false;
                    break;
            }
        }

        return true;
    }

    private static bool ParseLevels(ReadOnlySpan<byte> bytes, List<Level> levels)
    {
        var r = new Reader(bytes);
        while (!r.Done)
        {
            if (!r.Tag(out var field, out var wire)) return false;
            if (field == 1 && wire == 2)
            {
                if (!r.Bytes(out var one)) return false;
                var level = new Level();
                var lr = new Reader(one);
                while (!lr.Done)
                {
                    if (!lr.Tag(out var lf, out var lw)) return false;
                    ulong v;
                    switch (lf, lw)
                    {
                        case (1, 0): if (!lr.Varint(out v)) return false; level.Quantity = (long)v; break;
                        case (2, 1): if (!lr.Fixed64(out level.Price)) return false; break;
                        case (3, 0): if (!lr.Varint(out v)) return false; level.Orders = (long)v; break;
                        case (4, 0): if (!lr.Varint(out v)) return false; level.Buy = v != 0; break;
                        default:
                            if (!lr.Skip(lw)) return false;
                            break;
                    }
                }

                levels.Add(level);
            }
            else if (!r.Skip(wire)) return false;
        }

        return true;
    }
}
