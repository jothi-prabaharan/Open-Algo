using System.Text;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>One price level of an order book frame: price in paise.</summary>
public sealed record NubraLevel(long Price, long Quantity, long Orders);

/// <summary>An index (or instrument-as-index) price frame entry (WebSocketMsgIndex).</summary>
public sealed record NubraIndex(string Name, string Exchange, long Timestamp, long Value, long High, long Low, long Volume, double ChangePercent, long PrevClose, long VolumeOi);

/// <summary>One instrument's order book frame entry (WebSocketMsgOrderBook).</summary>
public sealed record NubraBook(long InstId, long Timestamp, IReadOnlyList<NubraLevel> Bids, IReadOnlyList<NubraLevel> Asks, long Ltp, long Ltq, long Volume, long RefId);

/// <summary>An index candle bucket frame entry (WebSocketMsgIndexBucket).</summary>
public sealed record NubraBucket(string Name, string Exchange, long Timestamp, long Open, long High, long Low, long Close, long BucketVolume, long CumulativeVolume);

/// <summary>An option chain item of a greeks frame (WebSocketMsgOptionChainItem): only the fields the feed reads.</summary>
public sealed record NubraGreeks(long RefId, long Oi, long PrevOi, long Ltp, long Volume);

/// <summary>What a Nubra binary frame holds: an Any wrapping an Any wrapping one batch message, told apart by the inner type url.</summary>
public sealed record NubraFrame(IReadOnlyList<NubraIndex> Indexes, IReadOnlyList<NubraBook> Books, IReadOnlyList<NubraBucket> Buckets, IReadOnlyList<NubraGreeks> Greeks)
{
    public static readonly NubraFrame Empty = new([], [], [], []);
}

/// <summary>
/// Nubra's protobuf frames (broker/nubra/protos/nubrafrontend.proto), read without a generated class: the wire format is varints and length-delimited fields. Anything that cannot be read gives an empty frame, as OpenAlgo's decoder drops
/// it. A field a message does not carry is nought or empty, as in protobuf.
/// </summary>
public static class NubraFrames
{
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int at;

        public readonly bool Done => at >= data.Length;

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (at >= data.Length) throw new FormatException("cut short");
                var b = data[at++];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return value;
            }

            throw new FormatException("varint too long");
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var length = (int)Varint();
            if (length < 0 || at + length > data.Length) throw new FormatException("cut short");
            var slice = data.Slice(at, length);
            at += length;
            return slice;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: at += 8; break;
                case 2: Bytes(); break;
                case 5: at += 4; break;
                default: throw new FormatException("wire type");
            }
        }

        public float Float32()
        {
            if (at + 4 > data.Length) throw new FormatException("cut short");
            var value = BitConverter.ToSingle(data.Slice(at, 4));
            at += 4;
            return value;
        }
    }

    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static NubraLevel Level(ReadOnlySpan<byte> bytes)
    {
        long price = 0, quantity = 0, orders = 0;
        var r = new Reader(bytes);
        while (!r.Done)
        {
            var tag = (int)r.Varint();
            switch (tag)
            {
                case (1 << 3): price = (long)r.Varint(); break;
                case (2 << 3): quantity = (long)r.Varint(); break;
                case (3 << 3): orders = (long)r.Varint(); break;
                default: r.Skip(tag & 7); break;
            }
        }

        return new NubraLevel(price, quantity, orders);
    }

    private static NubraIndex Index(ReadOnlySpan<byte> bytes)
    {
        string name = "", exchange = "";
        long ts = 0, value = 0, high = 0, low = 0, volume = 0, prev = 0, oi = 0;
        double pct = 0;
        var r = new Reader(bytes);
        while (!r.Done)
        {
            var tag = (int)r.Varint();
            switch (tag)
            {
                case (1 << 3 | 2): name = Text(r.Bytes()); break;
                case (2 << 3): ts = (long)r.Varint(); break;
                case (3 << 3): value = (long)r.Varint(); break;
                case (4 << 3): high = (long)r.Varint(); break;
                case (5 << 3): low = (long)r.Varint(); break;
                case (6 << 3): volume = (long)r.Varint(); break;
                case (7 << 3 | 5): pct = r.Float32(); break;
                case (9 << 3): prev = (long)r.Varint(); break;
                case (10 << 3 | 2): exchange = Text(r.Bytes()); break;
                case (11 << 3): oi = (long)r.Varint(); break;
                default: r.Skip(tag & 7); break;
            }
        }

        return new NubraIndex(name, exchange, ts, value, high, low, volume, pct, prev, oi);
    }

    private static NubraBook Book(ReadOnlySpan<byte> bytes)
    {
        long inst = 0, ts = 0, ltp = 0, ltq = 0, volume = 0, refId = 0;
        var bids = new List<NubraLevel>();
        var asks = new List<NubraLevel>();
        var r = new Reader(bytes);
        while (!r.Done)
        {
            var tag = (int)r.Varint();
            switch (tag)
            {
                case (1 << 3): inst = (long)r.Varint(); break;
                case (2 << 3): ts = (long)r.Varint(); break;
                case (3 << 3 | 2): bids.Add(Level(r.Bytes())); break;
                case (4 << 3 | 2): asks.Add(Level(r.Bytes())); break;
                case (5 << 3): ltp = (long)r.Varint(); break;
                case (6 << 3): ltq = (long)r.Varint(); break;
                case (7 << 3): volume = (long)r.Varint(); break;
                case (8 << 3): refId = (long)r.Varint(); break;
                default: r.Skip(tag & 7); break;
            }
        }

        return new NubraBook(inst, ts, bids, asks, ltp, ltq, volume, refId);
    }

    private static NubraBucket Bucket(ReadOnlySpan<byte> bytes)
    {
        string name = "", exchange = "";
        long ts = 0, open = 0, high = 0, low = 0, close = 0, bucket = 0, cumulative = 0;
        var r = new Reader(bytes);
        while (!r.Done)
        {
            var tag = (int)r.Varint();
            switch (tag)
            {
                case (1 << 3 | 2): name = Text(r.Bytes()); break;
                case (2 << 3 | 2): exchange = Text(r.Bytes()); break;
                case (4 << 3): ts = (long)r.Varint(); break;
                case (5 << 3): open = (long)r.Varint(); break;
                case (6 << 3): high = (long)r.Varint(); break;
                case (7 << 3): low = (long)r.Varint(); break;
                case (8 << 3): close = (long)r.Varint(); break;
                case (9 << 3): bucket = (long)r.Varint(); break;
                case (11 << 3): cumulative = (long)r.Varint(); break;
                default: r.Skip(tag & 7); break;
            }
        }

        return new NubraBucket(name, exchange, ts, open, high, low, close, bucket, cumulative);
    }

    private static NubraGreeks Greek(ReadOnlySpan<byte> bytes)
    {
        long ref_ = 0, oi = 0, prev = 0, ltp = 0, volume = 0;
        var r = new Reader(bytes);
        while (!r.Done)
        {
            var tag = (int)r.Varint();
            switch (tag)
            {
                case (5 << 3): ltp = (long)r.Varint(); break;
                case (12 << 3): oi = (long)r.Varint(); break;
                case (13 << 3): volume = (long)r.Varint(); break;
                case (14 << 3): ref_ = (long)r.Varint(); break;
                case (15 << 3): prev = (long)r.Varint(); break;
                default: r.Skip(tag & 7); break;
            }
        }

        return new NubraGreeks(ref_, oi, prev, ltp, volume);
    }

    /// <summary>The field 2 of an Any, its value; and the type url when asked.</summary>
    private static (string Url, byte[] Value) Any(ReadOnlySpan<byte> bytes)
    {
        var url = "";
        byte[] value = [];
        var r = new Reader(bytes);
        while (!r.Done)
        {
            var tag = (int)r.Varint();
            switch (tag)
            {
                case (1 << 3 | 2): url = Text(r.Bytes()); break;
                case (2 << 3 | 2): value = r.Bytes().ToArray(); break;
                default: r.Skip(tag & 7); break;
            }
        }

        return (url, value);
    }

    public static NubraFrame Decode(ReadOnlySpan<byte> raw)
    {
        try
        {
            var inner = Any(Any(raw).Value);
            var indexes = new List<NubraIndex>();
            var books = new List<NubraBook>();
            var buckets = new List<NubraBucket>();
            var greeks = new List<NubraGreeks>();
            var r = new Reader(inner.Value);
            if (inner.Url.EndsWith("BatchWebSocketIndexMessage", StringComparison.Ordinal))
            {
                while (!r.Done)
                {
                    var tag = (int)r.Varint();
                    if (tag == (2 << 3 | 2) || tag == (3 << 3 | 2)) indexes.Add(Index(r.Bytes()));
                    else r.Skip(tag & 7);
                }
            }
            else if (inner.Url.EndsWith("BatchWebSocketOrderbookMessage", StringComparison.Ordinal))
            {
                while (!r.Done)
                {
                    var tag = (int)r.Varint();
                    if (tag == (2 << 3 | 2)) books.Add(Book(r.Bytes()));
                    else r.Skip(tag & 7);
                }
            }
            else if (inner.Url.EndsWith("BatchWebSocketIndexBucketMessage", StringComparison.Ordinal))
            {
                while (!r.Done)
                {
                    var tag = (int)r.Varint();
                    if (tag == (2 << 3 | 2) || tag == (3 << 3 | 2)) buckets.Add(Bucket(r.Bytes()));
                    else r.Skip(tag & 7);
                }
            }
            else if (inner.Url.EndsWith("BatchWebSocketGreeksMessage", StringComparison.Ordinal))
            {
                while (!r.Done)
                {
                    var tag = (int)r.Varint();
                    if (tag == (2 << 3 | 2)) greeks.Add(Greek(r.Bytes()));
                    else r.Skip(tag & 7);
                }
            }

            return new NubraFrame(indexes, books, buckets, greeks);
        }
        catch (FormatException)
        {
            return NubraFrame.Empty;
        }
    }
}
