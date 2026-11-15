using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>One side of a book level in an mStock packet.</summary>
public sealed record MstockLevel(double Price, ulong Quantity, int Orders);

/// <summary>A quote read from one mStock packet (broker/mstock api/mstockwebsocket.py): the packet's own mode and exchange byte, the instrument token and the figures it carries (prices come in paise).</summary>
public sealed class MstockQuote
{
    public int Mode { get; init; }

    public int ExchangeType { get; init; }

    public string Token { get; init; } = "";

    public double Ltp { get; init; }

    public ulong LastTradedQuantity { get; init; }

    public double AveragePrice { get; init; }

    public ulong Volume { get; init; }

    public double TotalBuyQuantity { get; init; }

    public double TotalSellQuantity { get; init; }

    public double Open { get; init; }

    public double High { get; init; }

    public double Low { get; init; }

    public double Close { get; init; }

    public ulong OpenInterest { get; init; }

    public double UpperCircuit { get; init; }

    public double LowerCircuit { get; init; }

    public List<MstockLevel> Bids { get; init; } = [];

    public List<MstockLevel> Asks { get; init; } = [];
}

/// <summary>
/// The binary frames of mStock's market-data socket. A bare packet is 51 bytes (last price), 123 (quote) or 379 (quote with five levels of depth); anything else starts with a header of a packet count and a packet size (both
/// little-endian 16-bit) and holds that many packets of that size. A header that cannot be right (a size of zero or past the data, a count of none or past what fits) is read as the data allows: one packet filling the rest, or as many
/// as fit. A packet is read by its own length: 51, 123 or 379 as they are, 383 or more as a four-byte lead and then a snapshot, any other length is dropped.
/// </summary>
public static class MstockPackets
{
    private static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8));

    private static double Paise(ReadOnlySpan<byte> data, int at) => U64(data, at) / 100.0;

    private static string? Token(ReadOnlySpan<byte> data)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(data.Slice(2, 25)).Trim('\0');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static MstockQuote? ReadPacket(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> packet;
        if (data.Length is 51 or 123 or 379) packet = data;
        else if (data.Length >= 383) packet = data.Slice(4, 379);
        else return null;
        if (Token(packet) is not { } token) return null;

        var quote = new
        {
            Mode = (int)packet[0],
            Exchange = (int)packet[1],
            Token = token,
            Ltp = Paise(packet, 43),
        };

        if (packet.Length == 51) return new MstockQuote { Mode = quote.Mode, ExchangeType = quote.Exchange, Token = quote.Token, Ltp = quote.Ltp };
        var bids = new List<MstockLevel>();
        var asks = new List<MstockLevel>();
        ulong openInterest = 0;
        double upper = 0, lower = 0;
        if (packet.Length == 379)
        {
            openInterest = U64(packet, 131);
            upper = Paise(packet, 347);
            lower = Paise(packet, 355);
            var depth = packet.Slice(147, 200);
            for (var i = 0; i < 5; i++)
            {
                bids.Add(Level(depth, i * 20));
                asks.Add(Level(depth, 100 + (i * 20)));
            }
        }

        return new MstockQuote
        {
            Mode = quote.Mode,
            ExchangeType = quote.Exchange,
            Token = quote.Token,
            Ltp = quote.Ltp,
            LastTradedQuantity = U64(packet, 51),
            AveragePrice = Paise(packet, 59),
            Volume = U64(packet, 67),
            TotalBuyQuantity = BinaryPrimitives.ReadDoubleLittleEndian(packet.Slice(75, 8)),
            TotalSellQuantity = BinaryPrimitives.ReadDoubleLittleEndian(packet.Slice(83, 8)),
            Open = Paise(packet, 91),
            High = Paise(packet, 99),
            Low = Paise(packet, 107),
            Close = Paise(packet, 115),
            OpenInterest = openInterest,
            UpperCircuit = upper,
            LowerCircuit = lower,
            Bids = bids,
            Asks = asks,
        };
    }

    private static MstockLevel Level(ReadOnlySpan<byte> depth, int at) => new(Paise(depth, at + 10), U64(depth, at + 2), BinaryPrimitives.ReadUInt16LittleEndian(depth.Slice(at + 18, 2)));

    /// <summary>Every quote a frame carries, in order. A frame that cannot be read gives none.</summary>
    public static List<MstockQuote> Read(ReadOnlySpan<byte> data)
    {
        var quotes = new List<MstockQuote>();
        if (data.Length is 51 or 123 or 379)
        {
            if (ReadPacket(data) is { } one) quotes.Add(one);
            return quotes;
        }

        if (data.Length < 4) return quotes;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
        int size = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
        if (size <= 0 || size > data.Length - 4)
        {
            var fallback = data.Length - 4;
            if (fallback <= 0) return quotes;
            size = fallback;
        }

        var available = (data.Length - 4) / size;
        if (count < 1 || count > available) count = available;
        for (var i = 0; i < count; i++)
        {
            var start = 4 + (i * size);
            if (ReadPacket(data.Slice(start, Math.Min(size, data.Length - start))) is { } quote) quotes.Add(quote);
        }

        return quotes;
    }
}

/// <summary>
/// mStock's market-data socket (broker/mstock/streaming). The address carries the keys; the connection is signed in by the text LOGIN:&lt;access token&gt; and nothing answers it, so it is usable the moment it is sent. A request is a JSON frame
/// (action 1 to start, 0 to stop; a mode; tokens grouped by exchange type). The adapter keeps one subscription for a token, at the highest mode asked for: a higher mode stops the earlier subscription first, and an unsubscribe that leaves a lower
/// mode asks for that one again. A token is published to every subscription on it (the exchange is not part of the match), each on the topic of its own mode, from a packet of whatever mode the broker sent. A new connection signs in again and repeats
/// every token at its highest mode.
/// </summary>
public sealed class MstockFeedCodec : IFeedCodec
{
    private static readonly Dictionary<string, int> ExchangeTypes = new(StringComparer.Ordinal)
    {
        ["NSE"] = 1, ["NFO"] = 2, ["BSE"] = 3, ["BFO"] = 4, ["CDS"] = 13, ["MCX"] = 5, ["NSE_INDEX"] = 1, ["BSE_INDEX"] = 3,
    };

    private sealed record Sub(string Symbol, string Exchange, string Token, int Mode, int ExchangeType);

    private sealed record Wire(string Token, int ExchangeType, int Mode);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly Dictionary<string, int> tokenModes = [];
    private readonly Dictionary<string, string> tokenCorrelations = [];
    private readonly OrderedDictionary<string, Wire> wire = [];
    private string accessToken = "";

    public string Broker => "mstock";

    /// <summary>Exchanges the stream does not name go to the cash market of NSE, as OpenAlgo does.</summary>
    private static int ExchangeType(string brokerExchange) => ExchangeTypes.GetValueOrDefault(brokerExchange, 1);

    public void Begin(EPTradeBot.Core.Brokers.BrokerSession session)
    {
        lock (gate) accessToken = session.AuthToken;
    }

    private static string Frame(int action, int mode, IEnumerable<(string Token, int ExchangeType)> tokens)
    {
        var groups = new OrderedDictionary<int, JsonArray>();
        foreach (var (token, exchangeType) in tokens)
        {
            if (!groups.TryGetValue(exchangeType, out var list)) groups[exchangeType] = list = [];
            list.Add(token);
        }

        var tokenList = new JsonArray();
        foreach (var (exchangeType, list) in groups) tokenList.Add(new JsonObject { ["exchangeType"] = exchangeType, ["tokens"] = list });
        return new JsonObject { ["action"] = action, ["params"] = new JsonObject { ["mode"] = mode, ["tokenList"] = tokenList } }.ToJsonString();
    }

    /// <summary>Stops the named subscriptions the socket holds, one frame a mode, and forgets them.</summary>
    private List<string> StopWire(IEnumerable<string> correlations)
    {
        var byMode = new OrderedDictionary<int, List<(string Corr, Wire Wire)>>();
        foreach (var corr in correlations)
        {
            if (!wire.TryGetValue(corr, out var held)) continue;
            if (!byMode.TryGetValue(held.Mode, out var list)) byMode[held.Mode] = list = [];
            list.Add((corr, held));
        }

        var frames = new List<string>();
        foreach (var (mode, entries) in byMode)
        {
            frames.Add(Frame(0, mode, entries.Select(e => (e.Wire.Token, e.Wire.ExchangeType))));
            foreach (var (corr, _) in entries) wire.Remove(corr);
        }

        return frames;
    }

    private void StartWire(IReadOnlyList<(string Token, int ExchangeType, int Mode, string? Old)> items, List<string> frames)
    {
        // The last entry for a token wins and keeps the place of the first.
        var latest = new OrderedDictionary<string, (string Token, int ExchangeType, int Mode, string? Old)>();
        foreach (var item in items) latest[item.Token] = item;

        var desired = new Dictionary<string, int>();
        foreach (var sub in subs.Values) desired[sub.Token] = Math.Max(desired.GetValueOrDefault(sub.Token), sub.Mode);
        foreach (var token in latest.Where(l => l.Value.Mode > desired.GetValueOrDefault(l.Key)).Select(l => l.Key).ToList()) latest.Remove(token);
        if (latest.Count == 0) return;

        frames.AddRange(StopWire(latest.Values.Where(l => l.Old is not null).Select(l => l.Old!)));
        var byMode = new OrderedDictionary<int, List<(string Token, int ExchangeType)>>();
        foreach (var (token, item) in latest)
        {
            if (!byMode.TryGetValue(item.Mode, out var list)) byMode[item.Mode] = list = [];
            list.Add((token, item.ExchangeType));
        }

        foreach (var (mode, tokens) in byMode)
        {
            frames.Add(Frame(1, mode, tokens));
            foreach (var (token, exchangeType) in tokens)
            {
                var corr = $"mstock_{token}_{mode}";
                wire[corr] = new Wire(token, exchangeType, mode);
                tokenCorrelations[token] = corr;
                tokenModes[token] = mode;
            }
        }
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queue = new List<(string Token, int ExchangeType, int Mode, string? Old)>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3) continue;
                var exchangeType = ExchangeType(r.BrokerExchange);
                subs[$"{r.Symbol}_{r.Exchange}_{mode}"] = new Sub(r.Symbol, r.Exchange, r.Token, mode, exchangeType);
                var highest = subs.Values.Where(s => s.Token == r.Token).Max(s => s.Mode);
                var current = tokenModes.GetValueOrDefault(r.Token);
                var queued = queue.Where(q => q.Token == r.Token).Select(q => q.Mode).DefaultIfEmpty(0).Max();
                if (highest > Math.Max(current, queued)) queue.Add((r.Token, exchangeType, highest, current > 0 ? tokenCorrelations.GetValueOrDefault(r.Token) : null));
            }

            var frames = new List<string>();
            StartWire(queue, frames);
            return frames;
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            foreach (var m in mode is { } one ? [one] : Enum.GetValues<StreamMode>())
            {
                if (!subs.Remove($"{target.Symbol}_{target.Exchange}_{(int)m}", out var removed)) continue;
                var remaining = subs.Values.Where(s => s.Token == removed.Token).Select(s => s.Mode).DefaultIfEmpty(0).Max();
                var current = tokenModes.GetValueOrDefault(removed.Token);
                if (remaining >= current) continue;
                tokenCorrelations.TryGetValue(removed.Token, out var corr);
                if (remaining > 0)
                {
                    tokenModes[removed.Token] = remaining;
                    if (corr is not null && wire.ContainsKey(corr)) frames.AddRange(StopWire([corr]));
                    var next = $"mstock_{removed.Token}_{remaining}";
                    frames.Add(Frame(1, remaining, [(removed.Token, removed.ExchangeType)]));
                    wire[next] = new Wire(removed.Token, removed.ExchangeType, remaining);
                    tokenCorrelations[removed.Token] = next;
                }
                else
                {
                    // OpenAlgo forgets the subscription's name before it looks for it, so it never tells the broker to stop a token whose last mode went; the broker keeps sending it. EP Trade bot stops it.
                    tokenModes.Remove(removed.Token);
                    tokenCorrelations.Remove(removed.Token);
                    if (corr is not null) frames.AddRange(StopWire([corr]));
                }
            }

            return frames;
        }
    }

    /// <summary>A new connection: signed in again, then every token at the highest mode asked for it. A token on two exchanges is one subscription, on the exchange of its first highest-mode entry.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string> { $"LOGIN:{accessToken}" };
            var desired = new OrderedDictionary<string, Sub>();
            foreach (var sub in subs.Values)
                if (!desired.TryGetValue(sub.Token, out var held) || sub.Mode > held.Mode) desired[sub.Token] = sub;

            tokenModes.Clear();
            tokenCorrelations.Clear();
            StartWire(desired.Select(d => (d.Key, d.Value.ExchangeType, d.Value.Mode, (string?)null)).ToList(), frames);
            return frames;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            tokenModes.Clear();
            tokenCorrelations.Clear();
            wire.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var quotes = MstockPackets.Read(frame);
        var published = new List<FeedPublish>();
        lock (gate)
        {
            foreach (var quote in quotes)
            {
                if (quote.Token.Length == 0) continue;
                var matches = subs.Values.Where(s => s.Token == quote.Token).ToList();
                if (matches.Count == 0) continue;
                var baseData = Normalize(quote, quote.Mode);
                foreach (var sub in matches)
                {
                    var data = (JsonObject)baseData.DeepClone();
                    data["symbol"] = sub.Symbol;
                    data["exchange"] = sub.Exchange;
                    data["mode"] = sub.Mode;
                    data["timestamp"] = nowMs;
                    published.Add(new FeedPublish($"{sub.Exchange}_{sub.Symbol}_{sub.Mode switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" }}", data));
                }
            }
        }

        return published;
    }

    private static JsonNode Whole(ulong value) => value <= long.MaxValue ? JsonValue.Create((long)value) : JsonValue.Create((double)value);

    private static long Cut(double value) => double.IsFinite(value) && Math.Abs(value) < 9.2e18 ? (long)Math.Truncate(value) : throw new OverflowException();

    private static JsonArray Levels(List<MstockLevel> levels) =>
        new(levels.Take(5).Select(l => (JsonNode?)new JsonObject { ["price"] = l.Price, ["quantity"] = Whole(l.Quantity), ["orders"] = l.Orders }).ToArray());

    /// <summary>The OpenAlgo shape of a packet, by the packet's own mode: a last price, then the quote figures from mode 2, then the book and the circuits at 3. A total that cannot be a whole number leaves only the last price at zero.</summary>
    private static JsonObject Normalize(MstockQuote q, int mode)
    {
        try
        {
            var data = new JsonObject { ["ltp"] = q.Ltp };
            if (mode >= 2)
            {
                data["open"] = q.Open;
                data["high"] = q.High;
                data["low"] = q.Low;
                data["close"] = q.Close;
                data["prev_close"] = q.Close;
                data["volume"] = Whole(q.Volume);
                data["oi"] = Whole(q.OpenInterest);
                data["last_trade_quantity"] = Whole(q.LastTradedQuantity);
                data["average_price"] = q.AveragePrice;
                data["total_buy_quantity"] = Cut(q.TotalBuyQuantity);
                data["total_sell_quantity"] = Cut(q.TotalSellQuantity);
            }

            if (mode == 3)
            {
                data["depth"] = new JsonObject { ["buy"] = Levels(q.Bids), ["sell"] = Levels(q.Asks) };
                data["total_buy_quantity"] = Cut(q.TotalBuyQuantity);
                data["total_sell_quantity"] = Cut(q.TotalSellQuantity);
                data["upper_circuit"] = q.UpperCircuit;
                data["lower_circuit"] = q.LowerCircuit;
            }

            return data;
        }
        catch (OverflowException)
        {
            return new JsonObject { ["ltp"] = 0 };
        }
    }
}
