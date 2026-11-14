using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Kotak Neo's SFeed (broker/kotak/streaming sfeed_websocket.py, sfeed_protocol.py and kotak_adapter.py). The control plane is JSON: an auth frame on connect, and nothing else is sent until its answer arrives, which carries the price
/// divider of each exchange; subscribes are batched into one frame per kind (quote, depth, index) and a subscribe is answered with the trading symbol of each scrip. The data is little-endian binary, several packets to a frame,
/// each led by its length. The adapter keeps a state per scrip and merges a partial tick over it, caches the last price and depth, and publishes to each mode that is subscribed; an index is asked for by its name, not its token.
/// </summary>
public sealed class KotakFeedCodec : IFeedCodec
{
    private const int HeaderSize = 9;
    private const int MaxSubscriptions = 3000;
    private const int MaxPending = 256;
    private const double InvalidPrice = 21474836.48;
    private const double InvalidVolume = 2147483648;

    private static readonly Dictionary<string, string[]> IndexNames = new(StringComparer.Ordinal)
    {
        ["NIFTY"] = ["Nifty 50"], ["NIFTY50"] = ["Nifty 50"], ["BANKNIFTY"] = ["Nifty Bank"], ["FINNIFTY"] = ["Nifty Fin Service"],
        ["MIDCPNIFTY"] = ["Nifty Mid Select", "Nifty Midcap Sel", "Nifty Midcap Select", "NIFTY MID SELECT"], ["NIFTYNXT50"] = ["Nifty Next 50"], ["INDIAVIX"] = ["India VIX"], ["SENSEX"] = ["SENSEX"], ["BANKEX"] = ["BANKEX"],
    };

    private static readonly Dictionary<string, int> ExchangeIds = new()
    {
        ["none"] = 0, ["nse_cm"] = 1, ["nse_fo"] = 2, ["cde_fo"] = 3, ["nse_com"] = 4, ["bse_cm"] = 5, ["bse_fo"] = 6, ["bse_cd"] = 7, ["bse_co"] = 8, ["mcx_fo"] = 9, ["ncd_co"] = 10,
    };

    private sealed class Level
    {
        public double Price { get; set; }

        public long Quantity { get; set; }

        public long Orders { get; set; }

        public Level Copy() => new() { Price = Price, Quantity = Quantity, Orders = Orders };
    }

    /// <summary>A tick as the adapter handles it: the quote fields, and a book when it is a depth tick (null when the tick carried none).</summary>
    private sealed class Tick
    {
        public string Tk { get; set; } = "";

        public string E { get; set; } = "";

        public string Ts { get; set; } = "";

        public double Ltp { get; set; }

        public double Open { get; set; }

        public double High { get; set; }

        public double Low { get; set; }

        public double PrevClose { get; set; }

        public double Volume { get; set; }

        public double Bid { get; set; }

        public double Ask { get; set; }

        public List<Level>? Bids { get; set; }

        public List<Level>? Asks { get; set; }

        public Tick Copy() => new() { Tk = Tk, E = E, Ts = Ts, Ltp = Ltp, Open = Open, High = High, Low = Low, PrevClose = PrevClose, Volume = Volume, Bid = Bid, Ask = Ask, Bids = Bids?.Select(l => l.Copy()).ToList(), Asks = Asks?.Select(l => l.Copy()).ToList() };
    }

    private sealed class Cached
    {
        public List<Level> Buy { get; init; } = [];

        public List<Level> Sell { get; init; } = [];

        public double Ltp { get; init; }
    }

    private sealed record Sub(string Symbol, string Exchange, int Mode, string Token);

    private sealed record Op(string Exchange, string Token, string SubType);

    private readonly Lock gate = new();

    // The adapter.
    private readonly OrderedDictionary<string, Sub> subscriptions = [];
    private readonly Dictionary<(string, string), (string Exchange, string Symbol)> mapping = [];
    private readonly Dictionary<(string, string), HashSet<int>> modes = [];
    private readonly Dictionary<string, Tick> state = [];
    private readonly Dictionary<(string, string), double> ltpCache = [];
    private readonly Dictionary<(string, string), Cached> depthCache = [];
    private readonly List<Op> queue = [];

    // The connection.
    private readonly List<string> pending = [];
    private readonly List<string> outgoing = [];
    private readonly HashSet<(string, string, string)> held = [];
    private readonly Dictionary<string, string> symbols = [];
    private Dictionary<int, double> dividers = [];
    private bool connected;
    private bool authenticated;
    private string? ucc;
    private string? sid;

    public string Broker => "kotak";

    public void Begin(EPTradeBot.Core.Brokers.BrokerSession session)
    {
        lock (gate)
        {
            ucc = string.IsNullOrWhiteSpace(session.Key("ApiKey")) ? null : session.Key("ApiKey");
            var parts = session.AuthToken.Split(":::");
            sid = parts.Length > 1 ? parts[1] : null;
        }
    }

    private static string KotakExchange(string exchange) => exchange switch
    {
        "NSE" or "nse" => "nse_cm", "BSE" or "bse" => "bse_cm", "NFO" or "nfo" => "nse_fo", "BFO" or "bfo" => "bse_fo", "CDS" or "cds" => "cde_fo", "BCD" or "bcd" => "bcs-fo", "MCX" or "mcx" => "mcx_fo",
        "NSE_INDEX" => "nse_cm", "BSE_INDEX" => "bse_cm", _ => exchange,
    };

    private static bool IsIndex(string exchange) => exchange.EndsWith("_INDEX", StringComparison.OrdinalIgnoreCase);

    private static string[] IndexCandidates(string symbol) => IndexNames.GetValueOrDefault(symbol.ToUpperInvariant(), [symbol]);

    private static string Family(string subType) => subType switch { "mws" or "mwu" => "quote", "dps" or "dpu" => "depth", "ifs" or "ifu" => "index", _ => subType };

    private static string Event(string subType) => subType switch
    {
        "mws" => "subscribeScrips", "dps" => "subscribeDepth", "ifs" => "subscribeIndices", "mwu" => "unsubscribeScrips", "dpu" => "unsubscribeDepth", _ => "unsubscribeIndices",
    };

    // ---- the connection ----------------------------------------------------------------------------------------

    private string AuthFrame(long nowMs) => new JsonObject
    {
        ["user"] = ucc ?? "neome", ["auth"] = string.IsNullOrEmpty(sid) ? "1" : sid, ["format"] = "native_batch", ["source"] = "NEOTRADEAPI", ["platform"] = "Web", ["version"] = "1.2.3", ["sdk_version"] = 2,
        ["sdk_date"] = "2026-08-07T09:41:17.667Z", ["conn_req_time"] = nowMs, ["sessionValidation"] = false,
    }.ToJsonString();

    /// <summary>A control frame goes at once on an accepted connection, else it waits for the answer (the oldest of 256 is dropped).</summary>
    private void Send(JsonObject frame, List<string> now)
    {
        if (connected && authenticated)
        {
            now.Add(frame.ToJsonString());
            return;
        }

        if (pending.Count >= MaxPending) pending.RemoveAt(0);
        pending.Add(frame.ToJsonString());
    }

    private void SubscribeBatch(List<(string Exchange, string Token)> scrips, string subType, List<string> now)
    {
        if (scrips.Count == 0) return;
        var added = scrips.Select(s => (s.Exchange, s.Token, subType)).Where(s => !held.Contains(s)).ToHashSet();
        if (held.Count + added.Count > MaxSubscriptions) return;
        foreach (var s in added) held.Add(s);
        Send(new JsonObject { ["event"] = Event(subType), ["inputtoken"] = string.Join(',', scrips.Select(s => $"{s.Exchange}|{s.Token}")), ["ack_symbol"] = true }, now);
    }

    private void UnsubscribeBatch(List<(string Exchange, string Token)> scrips, string subType, List<string> now)
    {
        if (scrips.Count == 0) return;
        var paired = subType switch { "mwu" => "mws", "dpu" => "dps", _ => "ifs" };
        foreach (var s in scrips) held.Remove((s.Exchange, s.Token, paired));
        foreach (var s in scrips)
        {
            if (!held.Any(h => h.Item1 == s.Exchange && h.Item2 == s.Token)) symbols.Remove($"{s.Exchange}|{s.Token}");
        }

        Send(new JsonObject { ["event"] = Event(subType), ["inputtoken"] = string.Join(',', scrips.Select(s => $"{s.Exchange}|{s.Token}")) }, now);
    }

    /// <summary>The batch timer firing: the queue grouped by kind, the last operation on a scrip within a kind winning, one frame a group.</summary>
    private void Drain(List<string> now)
    {
        if (queue.Count == 0) return;
        var latest = new OrderedDictionary<(string, string, string, string), string>();
        foreach (var op in queue) latest[(op.Exchange, op.Token, "1", Family(op.SubType))] = op.SubType;
        var groups = new OrderedDictionary<string, List<(string, string)>>();
        foreach (var ((exchange, token, _, _), subType) in latest)
        {
            if (!groups.TryGetValue(subType, out var list)) groups[subType] = list = [];
            list.Add((exchange, token));
        }

        queue.Clear();
        foreach (var (subType, scrips) in groups)
        {
            var unsubscribe = subType is "mwu" or "dpu" or "ifu";
            for (var i = 0; i < scrips.Count; i += MaxSubscriptions)
            {
                var chunk = scrips.Skip(i).Take(MaxSubscriptions).ToList();
                if (unsubscribe) UnsubscribeBatch(chunk, subType, now);
                else SubscribeBatch(chunk, subType, now);
            }
        }
    }

    // ---- subscribing -------------------------------------------------------------------------------------------

    private string[] IndexFeedNames(string kotakExchange, string exchange, string symbol, string token)
    {
        var candidates = IndexCandidates(symbol);
        foreach (var key in new[] { token }.Concat(candidates))
        {
            mapping[(kotakExchange, key)] = (exchange, symbol);
            if (!modes.ContainsKey((kotakExchange, key))) modes[(kotakExchange, key)] = [];
        }

        var shared = modes[(kotakExchange, token)];
        foreach (var key in candidates) modes[(kotakExchange, key)] = shared;
        return candidates[..1];
    }

    private void Register(string kotakExchange, string token, string exchange, string symbol, int mode)
    {
        mapping[(kotakExchange, token)] = (exchange, symbol);
        if (!modes.TryGetValue((kotakExchange, token), out var set)) modes[(kotakExchange, token)] = set = [];
        set.Add(mode);
    }

    private void SubscribeQuote(string exchange, string symbol, string token, int mode)
    {
        var kex = KotakExchange(exchange);
        Register(kex, token, exchange, symbol, mode);
        if (IsIndex(exchange))
        {
            foreach (var name in IndexFeedNames(kex, exchange, symbol, token)) queue.Add(new Op(kex, name, "ifs"));
        }
        else
        {
            queue.Add(new Op(kex, token, "mws"));
        }
    }

    private void SubscribeDepth(string exchange, string symbol, string token, int mode)
    {
        var kex = KotakExchange(exchange);
        Register(kex, token, exchange, symbol, mode);
        if (IsIndex(exchange))
        {
            foreach (var name in IndexFeedNames(kex, exchange, symbol, token)) queue.Add(new Op(kex, name, "ifs"));
            return;
        }

        queue.Add(new Op(kex, token, "dps"));
    }

    private void SubscribeOne(string symbol, string exchange, string token, int mode)
    {
        if (mode is 1 or 2)
        {
            SubscribeQuote(exchange, symbol, token, mode);
        }
        else if (mode == 3)
        {
            SubscribeDepth(exchange, symbol, token, mode);
            SubscribeQuote(exchange, symbol, token, mode);
        }
        else
        {
            return;
        }

        subscriptions[$"{exchange}|{symbol}|{mode}"] = new Sub(symbol, exchange, mode, token);
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var now = new List<string>();
            foreach (var r in requests) SubscribeOne(r.Symbol, r.Exchange, r.Token, (int)r.Mode);
            Drain(now);
            return now;
        }
    }

    private void Release(string exchange, string symbol, string token, int mode, bool depth)
    {
        var kex = KotakExchange(exchange);
        var key = (kex, token);
        var unsubscribe = false;
        if (modes.TryGetValue(key, out var set))
        {
            set.Remove(mode);
            unsubscribe = depth ? !set.Contains(3) : !set.Overlaps([1, 2]);
            if (set.Count == 0)
            {
                mapping.Remove(key);
                modes.Remove(key);
                state.Remove($"{kex}|{token}");
            }
        }

        if (!unsubscribe) return;
        if (IsIndex(exchange))
        {
            foreach (var name in IndexFeedNames(kex, exchange, symbol, token)) queue.Add(new Op(kex, name, "ifu"));
        }
        else
        {
            queue.Add(new Op(kex, token, depth ? "dpu" : "mwu"));
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var now = new List<string>();
            foreach (var m in mode is { } one ? [one] : Enum.GetValues<StreamMode>())
            {
                var number = (int)m;
                if (number == 3) Release(target.Exchange, target.Symbol, target.Token, 3, depth: true);
                Release(target.Exchange, target.Symbol, target.Token, number, depth: false);
                subscriptions.Remove($"{target.Exchange}|{target.Symbol}|{number}");
                var key = (KotakExchange(target.Exchange), target.Token);
                if (!modes.TryGetValue(key, out var set) || set.Count == 0)
                {
                    ltpCache.Remove((target.Exchange, target.Symbol));
                    depthCache.Remove((target.Exchange, target.Symbol));
                }
            }

            Drain(now);
            return now;
        }
    }

    /// <summary>A new connection: the auth frame goes at once, and every subscription is asked for again, to wait for the answer.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var saved = subscriptions.Values.ToList();
            connected = true;
            authenticated = false;
            dividers = [];
            symbols.Clear();
            pending.Clear();
            held.Clear();
            state.Clear();
            var now = new List<string> { AuthFrame(nowMs) };
            foreach (var sub in saved) SubscribeOne(sub.Symbol, sub.Exchange, sub.Token, sub.Mode);

            Drain(now);
            return now;
        }
    }

    public IReadOnlyList<string> TakeOutgoing()
    {
        lock (gate)
        {
            var taken = outgoing.ToList();
            outgoing.Clear();
            return taken;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            subscriptions.Clear();
            mapping.Clear();
            modes.Clear();
            state.Clear();
            ltpCache.Clear();
            depthCache.Clear();
            queue.Clear();
            pending.Clear();
            outgoing.Clear();
            held.Clear();
            symbols.Clear();
            dividers = [];
            connected = false;
            authenticated = false;
        }
    }

    // ---- control frames ----------------------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(frame);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        if (parsed is not JsonObject data) return [];
        lock (gate)
        {
            var code = data["message_code"] is JsonValue v && v.TryGetValue<double>(out var c) ? (int?)c : null;
            if (code is 1117 or 1119)
            {
                if (data["format"] is JsonValue f && f.TryGetValue<string>(out var format) && format == "native_fallback") return [];
                var table = new Dictionary<int, double>();
                if (data["exchanges"] is JsonObject exchanges)
                {
                    foreach (var (name, info) in exchanges)
                    {
                        if (info is not JsonObject entry) continue;
                        int? id = entry.TryGetPropertyValue("value", out var value) ? value is JsonValue iv && iv.TryGetValue<double>(out var d) ? (int)d : null : ExchangeIds.TryGetValue(name, out var known) ? known : null;
                        if (id is null) continue;
                        table[id.Value] = entry["divider"] is JsonValue dv && dv.TryGetValue<double>(out var divider) ? divider : 100;
                    }
                }

                dividers = table;
                authenticated = true;
                outgoing.AddRange(pending);
                pending.Clear();
                return [];
            }

            if (code == 1109)
            {
                var map = data["trading_symbols"] is JsonObject a && a.Count > 0 ? a : data["tradingSymbols"] as JsonObject;
                if (map is null) return [];
                foreach (var (key, value) in map) symbols[key] = value is JsonValue sv && sv.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "None";
            }

            return [];
        }
    }

    // ---- binary frames -----------------------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        lock (gate)
        {
            if (!authenticated) return [];
            var published = new List<FeedPublish>();
            var offset = 0;
            var packets = new List<byte[]>();
            while (offset + 2 <= frame.Length)
            {
                var size = BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
                if (size < HeaderSize || offset + size > frame.Length) break;
                packets.Add(frame.Slice(offset, size).ToArray());
                offset += size;
            }

            foreach (var packet in packets)
            {
                Tick? tick;
                try
                {
                    tick = Decode(packet);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    break;
                }

                if (tick is not null) published.AddRange(OnData(tick, nowMs));
            }

            return published;
        }
    }

    private string SymbolFor(string segment, string token) => symbols.GetValueOrDefault($"{segment}|{token}", "");

    private Tick? Decode(byte[] packet)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0));
        var code = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
        var exchangeId = (sbyte)packet[4];
        var level = packet[5];
        var segment = ExchangeIds.FirstOrDefault(kv => kv.Value == exchangeId).Key ?? exchangeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var divider = dividers.TryGetValue(exchangeId, out var d) && d != 0 ? d : 100.0;
        var body = packet.AsSpan(HeaderSize);

        if (code is 6511 or 6521 or 105 or 104) return null;
        if (code == 7207) return Index(body, segment, divider);
        if (level == 1) return Mini(body, segment, divider);
        if (level is 2 or 4 or 8 or 16) return Picture(packet, body, segment, divider, level, length);
        return null;
    }

    private Tick Index(ReadOnlySpan<byte> b, string segment, double divider)
    {
        var token = BinaryPrimitives.ReadUInt32LittleEndian(b[0..]);
        var open = BinaryPrimitives.ReadInt32LittleEndian(b[4..]);
        var close = BinaryPrimitives.ReadInt32LittleEndian(b[8..]);
        var high = BinaryPrimitives.ReadInt32LittleEndian(b[12..]);
        var low = BinaryPrimitives.ReadInt32LittleEndian(b[16..]);
        var value = BinaryPrimitives.ReadInt32LittleEndian(b[20..]);
        var raw = b.Slice(57, 21);
        var end = raw.IndexOf((byte)0);
        var name = Encoding.UTF8.GetString(end < 0 ? raw : raw[..end]).Trim();
        var text = token.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Tick { Tk = text, E = segment, Ts = name.Length > 0 ? name : SymbolFor(segment, text), Ltp = value / divider, Open = open / divider, High = high / divider, Low = low / divider, PrevClose = close / divider };
    }

    private Tick Mini(ReadOnlySpan<byte> b, string segment, double divider)
    {
        var token = BinaryPrimitives.ReadUInt32LittleEndian(b[0..]);
        var price = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]);
        var close = BinaryPrimitives.ReadUInt32LittleEndian(b[24..]);
        var text = token.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Tick { Tk = text, E = segment, Ts = SymbolFor(segment, text), Ltp = price / divider, PrevClose = close / divider };
    }

    private Tick Picture(byte[] packet, ReadOnlySpan<byte> b, string segment, double divider, byte level, int length)
    {
        var token = BinaryPrimitives.ReadUInt32LittleEndian(b[0..]);
        var volume = BinaryPrimitives.ReadInt64LittleEndian(b[20..]);
        var open = BinaryPrimitives.ReadUInt32LittleEndian(b[44..]);
        var close = BinaryPrimitives.ReadUInt32LittleEndian(b[48..]);
        var high = BinaryPrimitives.ReadUInt32LittleEndian(b[52..]);
        var low = BinaryPrimitives.ReadUInt32LittleEndian(b[56..]);
        var price = BinaryPrimitives.ReadUInt32LittleEndian(b[60..]);
        var buyCount = BinaryPrimitives.ReadUInt32LittleEndian(b[84..]);
        var sellCount = BinaryPrimitives.ReadUInt32LittleEndian(b[88..]);
        _ = b[134];
        var (buyN, sellN) = level == 4 ? (1u, 1u) : (buyCount, sellCount);
        var buy = new List<Level>();
        var sell = new List<Level>();
        var offset = 144;
        var limit = Math.Min(length, packet.Length);
        for (var i = 0u; i < buyN + sellN; i++)
        {
            if (offset + 16 > limit) break;
            var row = new Level { Quantity = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(offset)), Price = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset + 8)) / divider, Orders = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset + 12)) };
            (i < buyN ? buy : sell).Add(row);
            offset += 16;
        }

        var text = token.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tick = new Tick
        {
            Tk = text, E = segment, Ts = SymbolFor(segment, text), Ltp = price / divider, Open = open / divider, High = high / divider, Low = low / divider, PrevClose = close / divider, Volume = volume,
            Bid = buy.Count > 0 ? buy[0].Price : 0.0, Ask = sell.Count > 0 ? sell[0].Price : 0.0,
        };
        if (level is 8 or 16)
        {
            tick.Bids = Padded(buy);
            tick.Asks = Padded(sell);
        }

        return tick;
    }

    private static List<Level> Padded(List<Level> rows)
    {
        var padded = rows.Take(5).Select(r => r.Copy()).ToList();
        while (padded.Count < 5) padded.Add(new Level());
        return padded;
    }

    // ---- the adapter -------------------------------------------------------------------------------------------

    private static bool IsPartial(Tick t)
    {
        if (t.Ltp > 0 && t.Ts.Length > 0) return false;
        var hasQuoteFields = t.Open != 0.0 || t.High != 0.0 || t.Low != 0.0 || t.PrevClose != 0.0;
        return !hasQuoteFields && t.Ts.Length == 0;
    }

    private static List<Level> MergeLevels(List<Level> now, List<Level> previous)
    {
        var merged = new List<Level>();
        for (var i = 0; i < 5; i++)
        {
            if (i < now.Count)
            {
                var p = i < previous.Count ? previous[i] : new Level();
                merged.Add(new Level { Price = now[i].Price != 0 ? now[i].Price : p.Price, Quantity = now[i].Quantity != 0 ? now[i].Quantity : p.Quantity, Orders = now[i].Orders != 0 ? now[i].Orders : p.Orders });
            }
            else if (i < previous.Count)
            {
                merged.Add(previous[i].Copy());
            }
            else
            {
                merged.Add(new Level());
            }
        }

        return merged;
    }

    private static JsonArray Book(List<Level> levels) => new(levels.Select(l => (JsonNode?)new JsonObject { ["price"] = l.Price, ["quantity"] = l.Quantity, ["orders"] = l.Orders }).ToArray());

    private List<FeedPublish> OnData(Tick incoming, long nowMs)
    {
        var tick = incoming;
        var token = tick.Tk;
        var kex = tick.E.Length > 0 ? tick.E : "UNKNOWN";
        var known = mapping.ContainsKey((kex, token));
        var byName = tick.Ts.Length > 0 && mapping.ContainsKey((kex, tick.Ts));
        if (!known && byName) token = tick.Ts;
        var hasDepth = tick.Bids is not null && tick.Asks is not null;
        var hasLtp = tick.Ltp > 0;
        var symbolKey = $"{kex}|{token}";
        var partial = IsPartial(tick);

        if (partial && !state.ContainsKey(symbolKey))
        {
            var initial = new Tick { Tk = tick.Tk, E = tick.E, Ts = tick.Ts.Length > 0 ? tick.Ts : "", Bids = [], Asks = [] };
            static double Valid(double v) => v != 0.0 && v != InvalidPrice ? v : 0.0;
            initial.Open = Valid(tick.Open);
            initial.High = Valid(tick.High);
            initial.Low = Valid(tick.Low);
            initial.PrevClose = Valid(tick.PrevClose);
            initial.Bid = Valid(tick.Bid);
            initial.Ask = Valid(tick.Ask);
            initial.Ltp = tick.Ltp > 0 ? tick.Ltp : 0.0;
            initial.Volume = tick.Volume != 0.0 && tick.Volume != InvalidVolume ? tick.Volume : 0.0;
            if (tick.Bids is not null) initial.Bids = tick.Bids.Select(l => l.Copy()).ToList();
            if (tick.Asks is not null) initial.Asks = tick.Asks.Select(l => l.Copy()).ToList();
            state[symbolKey] = initial;
        }

        if (hasDepth)
        {
            state.TryGetValue(symbolKey, out var previous);
            tick.Bids = MergeLevels(tick.Bids!, previous?.Bids ?? []);
            tick.Asks = MergeLevels(tick.Asks!, previous?.Asks ?? []);
        }

        if (partial && state.TryGetValue(symbolKey, out var stored))
        {
            var merged = stored.Copy();
            merged.Tk = tick.Tk;
            merged.E = tick.E;
            if (tick.Open != 0.0) merged.Open = tick.Open;
            if (tick.High != 0.0) merged.High = tick.High;
            if (tick.Low != 0.0) merged.Low = tick.Low;
            if (tick.PrevClose != 0.0) merged.PrevClose = tick.PrevClose;
            if (tick.Bid != 0.0) merged.Bid = tick.Bid;
            if (tick.Ask != 0.0) merged.Ask = tick.Ask;
            if (tick.Ltp != 0.0) merged.Ltp = tick.Ltp;
            if (tick.Volume != 0.0) merged.Volume = tick.Volume;
            if (tick.Ts.Length > 0) merged.Ts = tick.Ts;
            if (tick.Bids is not null) merged.Bids = tick.Bids;
            if (tick.Asks is not null) merged.Asks = tick.Asks;
            tick = merged;
            hasDepth = tick.Bids is not null && tick.Asks is not null;
            hasLtp = tick.Ltp > 0;
        }

        if (mapping.ContainsKey((kex, token)))
        {
            var kept = tick.Copy();
            kept.Bids ??= [];
            kept.Asks ??= [];
            state[symbolKey] = kept;
        }

        var published = new List<FeedPublish>();
        if (!hasLtp && !hasDepth) return published;
        if (!mapping.TryGetValue((kex, token), out var target)) return published;
        var (exchange, symbol) = target;
        var cacheKey = (exchange, symbol);
        if (hasLtp) ltpCache[cacheKey] = tick.Ltp;
        var cachedLtp = ltpCache.GetValueOrDefault(cacheKey, 0.0);
        if (hasDepth) depthCache[cacheKey] = new Cached { Buy = tick.Bids!, Sell = tick.Asks!, Ltp = hasLtp ? tick.Ltp : cachedLtp };
        var active = modes.TryGetValue((kex, token), out var set) ? set.Order().ToList() : [];
        var effective = hasLtp ? tick.Ltp : cachedLtp;
        var local = !hasDepth ? depthCache.GetValueOrDefault(cacheKey) : null;

        foreach (var mode in active)
        {
            JsonObject data;
            if (mode == 1 && hasLtp)
            {
                data = new JsonObject { ["ltp"] = tick.Ltp, ["ltt"] = nowMs };
            }
            else if (mode == 2)
            {
                data = new JsonObject { ["ltp"] = effective, ["ltt"] = nowMs, ["volume"] = tick.Volume, ["open"] = tick.Open, ["high"] = tick.High, ["low"] = tick.Low, ["close"] = tick.PrevClose };
            }
            else if (mode == 3)
            {
                if (exchange.EndsWith("_INDEX", StringComparison.Ordinal) && effective == 0) continue;
                List<Level> buy, sell;
                if (hasDepth)
                {
                    buy = tick.Bids!;
                    sell = tick.Asks!;
                }
                else if (local is not null)
                {
                    buy = local.Buy;
                    sell = local.Sell;
                }
                else if (exchange.EndsWith("_INDEX", StringComparison.Ordinal) && hasLtp)
                {
                    buy = [];
                    sell = [];
                }
                else
                {
                    continue;
                }

                data = new JsonObject { ["timestamp"] = nowMs, ["ltp"] = effective, ["depth"] = new JsonObject { ["buy"] = Book(buy), ["sell"] = Book(sell) }, ["totalbuyqty"] = 0, ["totalsellqty"] = 0 };
            }
            else
            {
                continue;
            }

            data["symbol"] = symbol;
            data["exchange"] = exchange;
            data["timestamp"] = nowMs;
            published.Add(new FeedPublish(MarketTopics.Format(exchange, symbol, (StreamMode)mode), data));
        }

        return published;
    }
}
