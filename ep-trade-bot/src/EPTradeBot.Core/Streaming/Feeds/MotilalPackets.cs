using System.Buffers.Binary;
using System.Text;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>One side of one level of Motilal's five-level book.</summary>
public sealed record MotilalLevel(double Price, long Quantity, long Orders);

/// <summary>The depth Motilal sent for a scrip: up to five levels a side, in level order. A level that has not arrived is null.</summary>
public sealed class MotilalDepth
{
    public MotilalLevel?[] Bids { get; } = new MotilalLevel?[5];

    public MotilalLevel?[] Asks { get; } = new MotilalLevel?[5];
}

/// <summary>
/// The wire of Motilal's broadcast feed (broker/motilal api/motilal_websocket.py): a binary login packet, binary register packets and 30-byte market packets, and what the client keeps of them. Nothing here opens a socket.
/// The store is keyed by the full Motilal exchange name and the scrip ("NSEFO:35001"): NSE and NSEFO share one wire character, so a packet is told apart by what was registered.
/// </summary>
public sealed class MotilalBook
{
    private const int PacketSize = 30;
    private readonly object gate = new();
    private readonly Dictionary<(string Char, int Scrip), string> scripExchange = [];
    private readonly Dictionary<string, Dictionary<string, double>> quotes = [];
    private readonly Dictionary<string, MotilalDepth> depths = [];
    private readonly Dictionary<string, Dictionary<string, double>> openInterest = [];
    private readonly Dictionary<string, double> indexRates = [];

    /// <summary>The single wire character of an exchange: NSE and NSEFO share N.</summary>
    public static string WireChar(string exchange)
    {
        var upper = exchange.ToUpperInvariant();
        return upper switch { "NSE" => "N", "BSE" => "B", "MCX" => "M", "NSECD" => "C", "NCDEX" => "D", "BSEFO" => "G", "NSEFO" => "N", _ => upper.Length > 0 ? upper[..1] : "" };
    }

    private static string CharToExchange(string c) => c switch { "N" => "NSE", "B" => "BSE", "M" => "MCX", "C" => "NSECD", "D" => "NCDEX", "G" => "BSEFO", _ => c };

    public static string Key(string exchange, object scrip) => $"{exchange.ToUpperInvariant()}:{scrip}";

    /// <summary>The login packet: 'Q', a fixed 111, the client code twice (length-prefixed in 15 and 30 byte fields), flags and the client version, 114 bytes in all.</summary>
    public static byte[] LoginPacket(string clientCode, string version = "1.0.0")
    {
        var bytes = new byte[114];
        var at = 0;
        bytes[at++] = (byte)'Q';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), 111);
        at += 2;
        bytes[at++] = (byte)clientCode.Length;
        Padded(clientCode, 15, bytes, ref at);
        bytes[at++] = (byte)clientCode.Length;
        Padded(clientCode, 30, bytes, ref at);
        bytes[at++] = 1;
        bytes[at++] = 1;
        bytes[at++] = 1;
        bytes[at++] = (byte)version.Length;
        Padded(version, 10, bytes, ref at);
        at += 4;
        bytes[at++] = 1;
        Padded("", 45, bytes, ref at);
        return bytes;
    }

    private static void Padded(string text, int width, byte[] into, ref int at)
    {
        var data = Encoding.UTF8.GetBytes(text.PadRight(width, ' '));
        Array.Copy(data, 0, into, at, Math.Min(data.Length, width));
        at += width;
    }

    /// <summary>The register (add) or unregister packet of a scrip: 'D', 7, the exchange character, the first letter of its segment, the scrip and a flag.</summary>
    public static byte[] RegisterPacket(string exchange, string segment, int scrip, bool add)
    {
        var bytes = new byte[10];
        bytes[0] = (byte)'D';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), 7);
        bytes[3] = (byte)WireChar(exchange)[0];
        bytes[4] = (byte)segment.ToUpperInvariant()[0];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), scrip);
        bytes[9] = add ? (byte)1 : (byte)0;
        return bytes;
    }

    /// <summary>The text frame that asks for an index's values (an index is not a scrip).</summary>
    public static string IndexFrame(string clientCode, string exchange, bool register) =>
        $"{{\"clientid\": \"{clientCode}\", \"action\": \"{(register ? "IndexRegister" : "IndexUnregister")}\", \"exchange\": \"{exchange}\"}}";

    /// <summary>Tells the book a scrip was registered, so its packets are stored under its own exchange. The first registration of a wire character and scrip wins.</summary>
    public void Register(string exchange, int scrip)
    {
        var upper = exchange.ToUpperInvariant();
        lock (gate) scripExchange.TryAdd((WireChar(upper), scrip), upper);
    }

    /// <summary>Forgets a scrip and the data that arrived for it.</summary>
    public void Unregister(string exchange, int scrip)
    {
        var upper = exchange.ToUpperInvariant();
        lock (gate)
        {
            var wire = WireChar(upper);
            if (scripExchange.TryGetValue((wire, scrip), out var held) && held == upper) scripExchange.Remove((wire, scrip));
            var key = Key(upper, scrip);
            quotes.Remove(key);
            depths.Remove(key);
            openInterest.Remove(key);
        }
    }

    /// <summary>Reads a frame: every whole 30-byte packet in it. Returns the store keys the frame touched, in order.</summary>
    public IReadOnlyList<string> Feed(ReadOnlySpan<byte> message)
    {
        var touched = new List<string>();
        lock (gate)
        {
            for (var i = 0; i < message.Length / PacketSize; i++)
            {
                var packet = message.Slice(i * PacketSize, PacketSize);
                var wire = packet[0] < 128 ? ((char)packet[0]).ToString() : "";
                var scrip = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(1, 4));
                var type = packet[9] < 128 ? ((char)packet[9]).ToString() : "";
                var body = packet.Slice(10, 20);
                var exchange = scripExchange.TryGetValue((wire, scrip), out var named) ? named : CharToExchange(wire);
                var key = Key(exchange, scrip);
                bool known = true;
                switch (type)
                {
                    case "B" or "C" or "D" or "E" or "F":
                        Depth(body, key, type[0] - 'B');
                        break;
                    case "A":
                        Ltp(body, key);
                        break;
                    case "G":
                        Ohlc(body, key);
                        break;
                    case "H":
                        Index(body, key);
                        break;
                    case "m":
                        Oi(body, key);
                        break;
                    case "W":
                        Dpr(body, key);
                        break;
                    default:
                        known = false;
                        break;
                }

                if (known) touched.Add(key);
            }
        }

        return touched;
    }



    private static double Rounded(ReadOnlySpan<byte> body, int at) => BookFormatting.RoundPy(BinaryPrimitives.ReadSingleLittleEndian(body.Slice(at, 4)), 2);

    private static int Whole(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadInt32LittleEndian(body.Slice(at, 4));

    private Dictionary<string, double> Quote(string key) => quotes.TryGetValue(key, out var q) ? q : quotes[key] = [];

    private void Depth(ReadOnlySpan<byte> body, string key, int level)
    {
        if (!depths.TryGetValue(key, out var depth)) depths[key] = depth = new MotilalDepth();
        depth.Bids[level] = new MotilalLevel(Rounded(body, 0), Whole(body, 4), BinaryPrimitives.ReadInt16LittleEndian(body.Slice(8, 2)));
        depth.Asks[level] = new MotilalLevel(Rounded(body, 10), Whole(body, 14), BinaryPrimitives.ReadInt16LittleEndian(body.Slice(18, 2)));
    }

    private void Ltp(ReadOnlySpan<byte> body, string key)
    {
        var q = Quote(key);
        q["ltp"] = Rounded(body, 0);
        q["ltq"] = Whole(body, 4);
        q["volume"] = Whole(body, 8);
        q["avg_trade_price"] = Rounded(body, 12);
        q["oi"] = q["open_interest"] = Whole(body, 16);
    }

    private void Dpr(ReadOnlySpan<byte> body, string key)
    {
        var q = Quote(key);
        q["upper_circuit"] = Rounded(body, 0);
        q["lower_circuit"] = Rounded(body, 4);
    }

    private void Ohlc(ReadOnlySpan<byte> body, string key)
    {
        var q = Quote(key);
        q["open"] = Rounded(body, 0);
        q["high"] = Rounded(body, 4);
        q["low"] = Rounded(body, 8);
        q["prev_close"] = Rounded(body, 12);
    }

    private void Oi(ReadOnlySpan<byte> body, string key)
    {
        var oi = Whole(body, 0);
        openInterest[key] = new Dictionary<string, double> { ["oi"] = oi, ["open_interest"] = oi, ["oi_high"] = Whole(body, 4), ["oi_low"] = Whole(body, 8) };
        if (quotes.TryGetValue(key, out var q)) q["oi"] = q["open_interest"] = oi;
    }

    private void Index(ReadOnlySpan<byte> body, string key)
    {
        var value = Rounded(body, 0);
        Quote(key)["ltp"] = value;
        indexRates[key] = value;
    }

    // ---- what a read takes out -------------------------------------------------------------------------------

    /// <summary>The quote of a scrip, or null.</summary>
    public IReadOnlyDictionary<string, double>? GetQuote(string exchange, object scrip)
    {
        lock (gate) return quotes.TryGetValue(Key(exchange, scrip), out var q) ? new Dictionary<string, double>(q) : null;
    }

    /// <summary>The depth of a scrip with the levels that have not arrived left out, or null.</summary>
    public (IReadOnlyList<MotilalLevel> Bids, IReadOnlyList<MotilalLevel> Asks)? GetDepth(string exchange, object scrip)
    {
        lock (gate)
        {
            if (!depths.TryGetValue(Key(exchange, scrip), out var d)) return null;
            return (d.Bids.OfType<MotilalLevel>().ToList(), d.Asks.OfType<MotilalLevel>().ToList());
        }
    }

    public IReadOnlyDictionary<string, double>? GetOpenInterest(string exchange, object scrip)
    {
        lock (gate) return openInterest.TryGetValue(Key(exchange, scrip), out var o) ? new Dictionary<string, double>(o) : null;
    }

    public double? GetIndexRate(string exchange, object index)
    {
        lock (gate) return indexRates.TryGetValue(Key(exchange, index), out var r) ? r : null;
    }

    /// <summary>Whether every packet a read uses has arrived for a scrip: the LTP and day OHLC packets, the open interest for a derivative, and the depth (all five levels, or level one with a price on both sides for a quote).</summary>
    public bool HasSnapshot(string exchange, object scrip, bool needOi, bool wholeBook)
    {
        lock (gate)
        {
            var key = Key(exchange, scrip);
            if (!quotes.TryGetValue(key, out var q) || !q.ContainsKey("ltp") || !q.ContainsKey("open")) return false;
            if (needOi && !openInterest.ContainsKey(key)) return false;
            if (!depths.TryGetValue(key, out var d)) return false;
            var full = d.Bids.All(b => b is not null) && d.Asks.All(a => a is not null);
            if (wholeBook || full) return full;
            var (bid, ask) = (d.Bids[0], d.Asks[0]);
            return bid is not null && ask is not null && bid.Price != 0 && ask.Price != 0;
        }
    }
}
