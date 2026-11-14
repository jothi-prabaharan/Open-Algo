using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Sahi's market-data stream (broker/sahi/streaming). Sahi speaks protobuf, one message per binary frame, and holds ONE mode per instrument id; an instrument wanted in several modes
/// is asked for at the highest and its tick, a superset of the lower modes, is fanned out to each. Changes are grouped: what Sahi holds is compared with what is wanted and the
/// difference goes as Unsubscribe frames first, then a ModeChange and a Subscribe per mode, each of at most 500 ids. Prices travel as paise.
/// </summary>
public sealed class SahiFeedCodec : IFeedCodec
{
    private const int MaxInstruments = 1000;
    private const int Chunk = 500;
    private const int DepthLevels = 5;
    private static readonly HashSet<string> Exchanges = ["NSE", "BSE", "NFO", "BFO", "NSE_INDEX", "BSE_INDEX"];

    private sealed class Sub(string symbol, string exchange)
    {
        public string Symbol { get; } = symbol;
        public string Exchange { get; } = exchange;
        public HashSet<StreamMode> Modes { get; set; } = [];
        public int Wanted => Modes.Count == 0 ? 0 : (int)Modes.Max();
    }

    private readonly Lock gate = new();
    private readonly Dictionary<long, Sub> subs = [];
    private readonly List<long> order = [];
    private readonly Dictionary<(string Symbol, string Exchange), long> keys = [];
    private readonly Dictionary<long, int> server = [];
    private readonly HashSet<long> dirty = [];

    public string Broker => "sahi";

    public bool SendsBinary => true;

    public bool MessagesAreHex => true;

    // ---- frames to Sahi ---------------------------------------------------------------------------------------

    private static void Varint(ulong value, List<byte> output)
    {
        while (value > 0x7F)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    private static byte[] Message(int field, int? mode, IReadOnlyList<long> ids)
    {
        var body = new List<byte>();
        if (mode is { } m and not 0)
        {
            Varint(1 << 3, body);
            Varint((ulong)m, body);
        }

        // An empty list is omitted; the ids travel packed.
        if (ids.Count > 0)
        {
            var packed = new List<byte>();
            foreach (var id in ids) Varint((ulong)id, packed);
            Varint((ulong)((mode is null ? 1 : 2) << 3 | 2), body);
            Varint((ulong)packed.Count, body);
            body.AddRange(packed);
        }

        var message = new List<byte>();
        Varint((ulong)(field << 3 | 2), message);
        Varint((ulong)body.Count, message);
        message.AddRange(body);
        return [.. message];
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static IEnumerable<long[]> Chunks(IEnumerable<long> ids) => ids.Chunk(Chunk);

    /// <summary>Brings what Sahi holds to the wanted mode of every changed instrument. The caller holds the lock.</summary>
    private List<string> Flush()
    {
        var frames = new List<string>();
        if (dirty.Count == 0) return frames;
        var changed = dirty.ToList();
        dirty.Clear();
        List<long> removes = [];
        SortedDictionary<int, List<long>> changes = [], adds = [];
        foreach (var id in changed)
        {
            var wanted = subs.TryGetValue(id, out var sub) ? sub.Wanted : 0;
            var held = server.GetValueOrDefault(id, 0);
            if (wanted == held) continue;
            if (wanted == 0)
            {
                removes.Add(id);
                server.Remove(id);
            }
            else if (held == 0)
            {
                if (!adds.TryGetValue(wanted, out var list)) adds[wanted] = list = [];
                list.Add(id);
                server[id] = wanted;
            }
            else
            {
                if (!changes.TryGetValue(wanted, out var list)) changes[wanted] = list = [];
                list.Add(id);
                server[id] = wanted;
            }
        }

        foreach (var ids in Chunks(removes.Order())) frames.Add(Hex(Message(2, null, ids)));
        foreach (var (mode, ids) in changes) foreach (var c in Chunks(ids.Order())) frames.Add(Hex(Message(3, mode, c)));
        foreach (var (mode, ids) in adds) foreach (var c in Chunks(ids.Order())) frames.Add(Hex(Message(1, mode, c)));
        return frames;
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            foreach (var r in requests)
            {
                if (!Enum.IsDefined(r.Mode) || !Exchanges.Contains(r.Exchange) || !long.TryParse(r.Token, out var id)) continue;
                if (!subs.TryGetValue(id, out var sub))
                {
                    if (subs.Count >= MaxInstruments) continue;
                    sub = new Sub(r.Symbol, r.Exchange);
                    subs[id] = sub;
                    order.Add(id);
                    keys[(r.Symbol, r.Exchange)] = id;
                }

                sub.Modes = [.. sub.Modes, r.Mode];
                dirty.Add(id);
            }

            return Flush();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            if (!keys.TryGetValue((target.Symbol, target.Exchange), out var id) || !subs.TryGetValue(id, out var sub)) return [];
            if (mode is { } m && !sub.Modes.Contains(m)) return [];
            sub.Modes = mode is null ? [] : [.. sub.Modes.Where(x => x != mode)];
            if (sub.Modes.Count == 0)
            {
                subs.Remove(id);
                order.Remove(id);
                keys.Remove((target.Symbol, target.Exchange));
            }

            dirty.Add(id);
            return Flush();
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            // A new connection holds nothing: the whole wanted state goes, which also settles every change still waiting.
            server.Clear();
            dirty.Clear();
            SortedDictionary<int, List<long>> byMode = [];
            foreach (var id in order)
            {
                var wanted = subs[id].Wanted;
                if (wanted == 0) continue;
                if (!byMode.TryGetValue(wanted, out var list)) byMode[wanted] = list = [];
                list.Add(id);
                server[id] = wanted;
            }

            return byMode.SelectMany(kv => Chunks(kv.Value).Select(c => Hex(Message(1, kv.Key, c)))).ToList();
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            order.Clear();
            keys.Clear();
            server.Clear();
            dirty.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- frames from Sahi -------------------------------------------------------------------------------------

    private sealed class DecodeError : Exception;

    private sealed record Tick(long InstrumentId, long Price, long? Time, long Volume, long Upper, long Lower, long? OpenInterest, List<(long Price, long Quantity, long Orders)> Buys, List<(long Price, long Quantity, long Orders)> Sells);

    private static ulong ReadVarint(byte[] buf, ref int pos, int end)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= end) throw new DecodeError();
            var b = buf[pos++];
            if (shift < 64) result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
            shift += 7;
            if (shift >= 70) throw new DecodeError();
        }
    }

    private static int Skip(byte[] buf, int pos, int end, int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(buf, ref pos, end);
                break;
            case 1:
                pos += 8;
                break;
            case 2:
                var length = ReadVarint(buf, ref pos, end);
                if (length > (ulong)int.MaxValue) throw new DecodeError();
                pos += (int)length;
                break;
            case 5:
                pos += 4;
                break;
            default:
                throw new DecodeError();
        }

        if (pos > end) throw new DecodeError();
        return pos;
    }

    private static (int Start, int Stop) ReadLength(byte[] buf, ref int pos, int end)
    {
        var length = ReadVarint(buf, ref pos, end);
        if (length > (ulong)(end - pos)) throw new DecodeError();
        var start = pos;
        pos += (int)length;
        return (start, pos);
    }

    private static (long Price, long Quantity, long Orders) ReadLevel(byte[] buf, int pos, int end)
    {
        long price = 0, quantity = 0, orders = 0;
        while (pos < end)
        {
            var key = ReadVarint(buf, ref pos, end);
            var (field, wt) = ((int)(key >> 3), (int)(key & 7));
            if (wt == 0 && field is 1 or 2 or 3)
            {
                var v = (long)ReadVarint(buf, ref pos, end);
                if (field == 1) price = v; else if (field == 2) quantity = v; else orders = v;
            }
            else pos = Skip(buf, pos, end, wt);
        }

        return (price, quantity, orders);
    }

    private static Tick ReadTick(byte[] buf, int pos, int end)
    {
        long id = 0, price = 0, volume = 0, upper = 0, lower = 0;
        long? time = null, oi = null;
        List<(long, long, long)> buys = [], sells = [];
        while (pos < end)
        {
            var key = ReadVarint(buf, ref pos, end);
            var (field, wt) = ((int)(key >> 3), (int)(key & 7));
            if (wt == 0 && field is >= 1 and <= 10)
            {
                var v = (long)ReadVarint(buf, ref pos, end);
                switch (field)
                {
                    case 1: id = v; break;
                    case 4: price = v; break;
                    case 5: time = v; break;
                    case 6: volume = v; break;
                    case 7: upper = v; break;
                    case 8: lower = v; break;
                    case 10: oi = v; break;
                }
            }
            else if (wt == 2 && field is 11 or 12)
            {
                var (start, stop) = ReadLength(buf, ref pos, end);
                (field == 11 ? buys : sells).Add(ReadLevel(buf, start, stop));
            }
            else pos = Skip(buf, pos, end, wt);
        }

        return new Tick(id, price, time, volume, upper, lower, oi, buys, sells);
    }

    /// <summary>Walks an ack or error payload only to find out whether it is well formed (a malformed one drops the whole frame).</summary>
    private static void Validate(byte[] buf, int pos, int end, bool ack)
    {
        while (pos < end)
        {
            var key = ReadVarint(buf, ref pos, end);
            var (field, wt) = ((int)(key >> 3), (int)(key & 7));
            if (ack && field == 1 && wt == 0) ReadVarint(buf, ref pos, end);
            else if (ack && field == 2 && wt == 2)
            {
                var (start, stop) = ReadLength(buf, ref pos, end);
                while (start < stop) ReadVarint(buf, ref start, stop);
            }
            else if (ack && field == 2 && wt == 0) ReadVarint(buf, ref pos, end);
            else if (!ack && field is 1 or 2 && wt == 2) ReadLength(buf, ref pos, end);
            else pos = Skip(buf, pos, end, wt);
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var buf = frame.ToArray();
        var pos = 0;
        var end = buf.Length;
        int kind = 0;
        Tick? tick = null;
        try
        {
            while (pos < end)
            {
                var key = ReadVarint(buf, ref pos, end);
                var (field, wt) = ((int)(key >> 3), (int)(key & 7));
                if (field is >= 1 and <= 5 && wt == 2)
                {
                    var (start, stop) = ReadLength(buf, ref pos, end);
                    kind = field;
                    if (field is 2 or 3 or 4) tick = ReadTick(buf, start, stop);
                    else Validate(buf, start, stop, field == 1);
                }
                else pos = Skip(buf, pos, end, wt);
            }
        }
        catch (DecodeError)
        {
            return [];
        }

        // Field 2 is an LTP tick, 3 a quote, 4 a depth tick; a message that last carried an ack, an error or nothing publishes nothing.
        if (kind is not (2 or 3 or 4) || tick is null) return [];
        return Publish(tick, kind - 1, nowMs);
    }

    private static double Rupees(long paise) => paise / 100.0;

    private static JsonArray Side(List<(long Price, long Quantity, long Orders)> levels)
    {
        var side = new JsonArray();
        foreach (var l in levels.Take(DepthLevels)) side.Add(new JsonObject { ["price"] = Rupees(l.Price), ["quantity"] = l.Quantity, ["orders"] = l.Orders });
        while (side.Count < DepthLevels) side.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 });
        return side;
    }

    private IReadOnlyList<FeedPublish> Publish(Tick tick, int level, long nowMs)
    {
        Sub sub;
        List<StreamMode> modes;
        lock (gate)
        {
            if (!subs.TryGetValue(tick.InstrumentId, out var known)) return [];
            sub = known;
            modes = [.. sub.Modes.Order()];
        }

        var published = new List<FeedPublish>();
        foreach (var mode in modes)
        {
            if ((int)mode > level) continue;
            var data = new JsonObject
            {
                ["symbol"] = sub.Symbol, ["exchange"] = sub.Exchange, ["mode"] = "ltp", ["ltp"] = Rupees(tick.Price), ["ltt"] = tick.Time ?? 0, ["timestamp"] = nowMs,
            };
            if (mode != StreamMode.Ltp)
            {
                data["mode"] = "quote";
                data["volume"] = tick.Volume;
                data["last_quantity"] = 0;
                data["average_price"] = 0;
                data["total_buy_quantity"] = 0;
                data["total_sell_quantity"] = 0;
                data["open"] = 0;
                data["high"] = 0;
                data["low"] = 0;
                data["close"] = 0;
                data["oi"] = tick.OpenInterest ?? 0;
                data["open_interest"] = tick.OpenInterest ?? 0;
                data["upper_circuit"] = Rupees(tick.Upper);
                data["lower_circuit"] = Rupees(tick.Lower);
            }

            if (mode == StreamMode.Depth)
            {
                data["mode"] = "full";
                data["depth"] = new JsonObject { ["buy"] = Side(tick.Buys), ["sell"] = Side(tick.Sells) };
            }

            published.Add(new FeedPublish(MarketTopics.Format(sub.Exchange, sub.Symbol, mode), data));
        }

        return published;
    }
}
