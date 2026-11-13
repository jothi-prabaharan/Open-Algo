using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Delta Exchange's public market-data socket (broker/deltaexchange/streaming): JSON over a websocket, no sign-in. LTP and Quote read the ticker channel;
/// Depth reads the order book channel as well, and the ticker, because the book carries no price. The ticker takes any number of symbols in one
/// message; the book takes one symbol per message. Prices arrive as text and a missing field is left out rather than sent as zero, so one channel
/// never blanks what the other published.
/// </summary>
public sealed class DeltaFeedCodec : IFeedCodec
{
    private const string Ticker = "ticker";
    private const string Book = "ob_l2";

    private sealed record Sub(string Symbol, string Exchange, string BrokerSymbol, int Mode, string[] Channels);

    private readonly Lock gate = new();
    private readonly List<Sub> subs = [];
    private readonly List<string> channelOrder = [];
    private readonly Dictionary<string, SortedSet<string>> registry = [];
    private readonly Dictionary<string, JsonObject> cache = [];

    public string Broker => "deltaexchange";

    private static string[] ChannelsOf(int mode) => mode switch { 1 => [Ticker], 2 => [Ticker], 3 => [Book, Ticker], _ => [Ticker] };

    private static string ModeName(int mode) => mode switch { 1 => "LTP", 2 => "QUOTE", 3 => "DEPTH", _ => "LTP" };

    private static string Frame(string verb, string channel, IEnumerable<string> symbols) =>
        new JsonObject
        {
            ["type"] = verb,
            ["payload"] = new JsonObject { ["channels"] = new JsonArray(new JsonObject { ["name"] = channel, ["symbols"] = new JsonArray(symbols.Select(s => (JsonNode?)s).ToArray()) }) },
        }.ToJsonString();

    /// <summary>The ticker takes the whole list in one message; the book is refused unless it carries a single symbol.</summary>
    private static IEnumerable<string> Frames(string verb, string channel, IReadOnlyList<string> symbols)
    {
        if (symbols.Count == 0) yield break;
        if (channel == Book)
        {
            foreach (var symbol in symbols) yield return Frame(verb, channel, [symbol]);
        }
        else
        {
            yield return Frame(verb, channel, symbols);
        }
    }

    private static List<(string Channel, List<string> Symbols)> Group(List<(string Channel, string Symbol)> queue)
    {
        var grouped = new List<(string Channel, List<string> Symbols)>();
        foreach (var (channel, symbol) in queue)
        {
            var at = grouped.FindIndex(g => g.Channel == channel);
            if (at < 0) grouped.Add((channel, [symbol]));
            else grouped[at].Symbols.Add(symbol);
        }

        return grouped;
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        var queue = new List<(string Channel, string Symbol)>();
        lock (gate)
        {
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3) continue;
                var brokerSymbol = r.BrokerSymbol is { Length: > 0 } b ? b : r.Symbol;
                var channels = ChannelsOf(mode);
                var entry = new Sub(r.Symbol, r.Exchange, brokerSymbol, mode, channels);
                var at = subs.FindIndex(s => s.Symbol == r.Symbol && s.Exchange == r.Exchange && s.Mode == mode);
                if (at < 0) subs.Add(entry);
                else subs[at] = entry;
                foreach (var channel in channels)
                {
                    if (!queue.Contains((channel, brokerSymbol))) queue.Add((channel, brokerSymbol));
                }
            }

            var frames = new List<string>();
            foreach (var (channel, symbols) in Group(queue))
            {
                if (!registry.TryGetValue(channel, out var active))
                {
                    registry[channel] = active = new SortedSet<string>(StringComparer.Ordinal);
                    channelOrder.Add(channel);
                }

                foreach (var symbol in symbols) active.Add(symbol);
                frames.AddRange(Frames("subscribe", channel, symbols));
            }

            return frames;
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            // A mode named is that mode; none named is every mode of the instrument that is subscribed.
            var modes = mode is { } named
                ? new List<int> { (int)named }
                : subs.Where(s => s.Symbol == target.Symbol && s.Exchange == target.Exchange).Select(s => s.Mode).ToList();

            var queue = new List<(string Channel, string Symbol)>();
            foreach (var m in modes)
            {
                var stored = subs.FirstOrDefault(s => s.Symbol == target.Symbol && s.Exchange == target.Exchange && s.Mode == m);
                if (stored is not null) subs.Remove(stored);
                // An instrument that was never subscribed is unsubscribed under its own symbol, as OpenAlgo does.
                var brokerSymbol = stored?.BrokerSymbol ?? target.Symbol;
                foreach (var channel in ChannelsOf(m))
                {
                    if (subs.Any(s => s.BrokerSymbol == brokerSymbol && s.Channels.Contains(channel))) continue;
                    if (!queue.Contains((channel, brokerSymbol))) queue.Add((channel, brokerSymbol));
                }

                if (!subs.Any(s => s.Symbol == target.Symbol && s.Exchange == target.Exchange)) cache.Remove($"{target.Symbol}_{target.Exchange}");
            }

            var frames = new List<string>();
            foreach (var (channel, symbols) in Group(queue))
            {
                if (registry.TryGetValue(channel, out var active)) active.ExceptWith(symbols);
                frames.AddRange(Frames("unsubscribe", channel, symbols));
            }

            return frames;
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            foreach (var channel in channelOrder)
            {
                if (registry[channel].Count > 0) frames.AddRange(Frames("subscribe", channel, [.. registry[channel]]));
            }

            return frames;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            channelOrder.Clear();
            registry.Clear();
            cache.Clear();
        }
    }

    // ---- reading what comes back -----------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(frame) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }

        if (message is null) return [];
        var type = message["type"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "";
        List<(string BrokerSymbol, JsonObject Fields)> updates;
        switch (type)
        {
            case Ticker: updates = Ticker_(message); break;
            case Book: updates = [BookUpdate(message)]; break;
            default: return [];
        }

        var published = new List<FeedPublish>();
        lock (gate)
        {
            foreach (var (brokerSymbol, fields) in updates)
            {
                if (brokerSymbol.Length == 0) continue;
                // Every subscription of this symbol on the channel; failing that, any subscription of the symbol.
                var matched = subs.Where(s => s.BrokerSymbol == brokerSymbol && s.Channels.Contains(type)).ToList();
                if (matched.Count == 0) matched = [.. subs.Where(s => s.BrokerSymbol == brokerSymbol)];
                if (matched.Count == 0) continue;

                var key = $"{matched[0].Symbol}_{matched[0].Exchange}";
                if (!cache.TryGetValue(key, out var cached)) cache[key] = cached = [];
                foreach (var (name, value) in fields) cached[name] = value?.DeepClone();

                foreach (var s in matched)
                {
                    var data = (JsonObject)cached.DeepClone();
                    data["symbol"] = s.Symbol;
                    data["exchange"] = s.Exchange;
                    data["mode"] = s.Mode;
                    data["timestamp"] = nowMs;
                    published.Add(new FeedPublish($"{s.Exchange}_{s.Symbol}_{ModeName(s.Mode)}", data));
                }
            }
        }

        return published;
    }

    /// <summary>A number as OpenAlgo's float(): a number or numeric text, else the default.</summary>
    private static double Float(JsonNode? node, double fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    /// <summary>A size as OpenAlgo's int(float()): truncated toward zero.</summary>
    private static long Whole(JsonNode? node) => (long)Math.Truncate(Float(node));

    private static JsonNode? At(JsonNode? sequence, int index) => sequence is JsonArray a && a.Count > index ? a[index] : null;

    private static List<(string BrokerSymbol, JsonObject Fields)> Ticker_(JsonObject message)
    {
        var updates = new List<(string, JsonObject)>();
        var spot = message["sp"];
        foreach (var entry in (message["d"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var brokerSymbol = Text(entry["s"]) is { Length: > 0 } s ? s : Text(message["sy"]);
            var ohlc = entry["ohlc"];
            var oi = entry["oi"];
            var quotes = entry["q"];
            var fields = new JsonObject { ["average_price"] = 0, ["oi_change"] = 0 };

            // A scalar left out is left out; a present array is authoritative, so a null inside it reads as zero.
            var mark = entry["m"] ?? spot;
            if (mark is not null) fields["ltp"] = Float(mark);
            if (ohlc is not null)
            {
                fields["open"] = Float(At(ohlc, 0));
                fields["high"] = Float(At(ohlc, 1));
                fields["low"] = Float(At(ohlc, 2));
                fields["close"] = Float(At(ohlc, 3));
            }

            if (oi is not null) fields["oi"] = Float(At(oi, 0));
            if (quotes is not null)
            {
                fields["bid_price"] = Float(At(quotes, 2));
                fields["bid_qty"] = Whole(At(quotes, 3));
                fields["ask_price"] = Float(At(quotes, 0));
                fields["ask_qty"] = Whole(At(quotes, 1));
            }

            updates.Add((brokerSymbol, fields));
        }

        return updates;
    }

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static (string, JsonObject) BookUpdate(JsonObject message)
    {
        static (JsonArray Levels, long Total) Levels(JsonNode? side)
        {
            var levels = new JsonArray();
            long total = 0;
            if (side is JsonArray list)
            {
                foreach (var level in list.Take(5))
                {
                    if (level is not JsonArray pair || pair.Count < 2) continue;
                    var quantity = Whole(pair[1]);
                    levels.Add(new JsonObject { ["price"] = Float(pair[0]), ["quantity"] = quantity });
                    total += quantity;
                }
            }

            while (levels.Count < 5) levels.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0 });
            return (levels, total);
        }

        var (bids, bidTotal) = Levels(message["b"]);
        var (asks, askTotal) = Levels(message["a"]);
        return (Text(message["sy"]), new JsonObject
        {
            ["depth"] = new JsonObject { ["buy"] = bids, ["sell"] = asks },
            ["totalbuyqty"] = bidTotal,
            ["totalsellqty"] = askTotal,
        });
    }
}
