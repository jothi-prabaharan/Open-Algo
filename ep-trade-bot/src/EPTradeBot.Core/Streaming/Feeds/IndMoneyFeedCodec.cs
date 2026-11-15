using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// IndMoney's stream (broker/indmoney/streaming). Subscriptions are JSON frames {action, mode ltp|quote, instruments [SEGMENT:TOKEN]} with the segment NSE, NFO, BSE, BFO, NIDX or BIDX; there is no depth stream, so depth and an exchange with no
/// segment (MCX, currency) are refused. A burst is grouped by mode and then segment (a frame mixing segments delivered ticks for the first only); a frame carries at most 1000 instruments and a connection holds 3000, counted over both modes, and a group
/// that would pass the limit is not sent at all. A reconnect asks for everything the client holds (by mode, then segment) after the adapter has asked for what it holds that the client does not, so some requests go twice, as OpenAlgo's do.
/// A tick is JSON {mode, instrument, timestamp, data}. It belongs to the first subscription holding the full instrument (SEGMENT:TOKEN), else the one subscription holding the bare token, and is dropped when two do. Its topic follows the mode
/// the tick says (ltp, otherwise quote) while its <c>mode</c> is the matched subscription's. A field that is missing or zero keeps the last value the symbol had, whichever mode brought it; any other value is published as it came, text included.
/// </summary>
public sealed class IndMoneyFeedCodec : IFeedCodec
{
    private const int MaxPerFrame = 1000;
    private const int MaxPerConnection = 3000;
    private static readonly string[] QuoteFields = ["ltp", "open", "high", "low", "close", "volume", "bid_price", "bid_qty", "ask_price", "ask_qty", "average_price", "oi", "oi_change"];

    private sealed record Sub(string Symbol, string Exchange, string Token, string Instrument, string Segment, int Mode, string ModeText);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, List<string>> client = [];
    private readonly OrderedDictionary<(string Symbol, string Exchange, int Mode), Sub> subs = [];
    private readonly Dictionary<string, Dictionary<string, JsonNode?>> remembered = [];
    private bool again;

    public string Broker => "indmoney";

    private static string? SegmentOf(string exchange) => exchange switch { "NSE" => "NSE", "NFO" => "NFO", "BSE" => "BSE", "BFO" => "BFO", "NSE_INDEX" => "NIDX", "BSE_INDEX" => "BIDX", _ => null };

    private static string ModeText(int mode) => mode == 2 ? "quote" : "ltp";

    private static string SegmentPart(string instrument) => instrument.Split(':', 2)[0];

    private static string Frame(string action, string mode, IEnumerable<string> instruments) =>
        new JsonObject { ["action"] = action, ["mode"] = mode, ["instruments"] = new JsonArray(instruments.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) }.ToJsonString();

    /// <summary>The client's own subscribe: what it does not hold yet, chunked by segment, refused whole when it would pass the limit.</summary>
    private List<string> ClientSubscribe(IEnumerable<string> instruments, string mode)
    {
        if (!client.TryGetValue(mode, out var held)) client[mode] = held = [];
        var have = held.ToHashSet(StringComparer.Ordinal);
        var fresh = instruments.Distinct(StringComparer.Ordinal).Where(i => !have.Contains(i)).ToList();
        if (fresh.Count == 0) return [];
        if (client.Values.Sum(l => l.Count) + fresh.Count > MaxPerConnection) return [];
        held.AddRange(fresh);
        again = true;
        return Chunks(fresh, mode);
    }

    private static List<string> Chunks(IReadOnlyList<string> instruments, string mode)
    {
        var frames = new List<string>();
        foreach (var segment in instruments.GroupBy(SegmentPart))
        {
            foreach (var chunk in segment.Chunk(MaxPerFrame)) frames.Add(Frame("subscribe", mode, chunk));
        }

        return frames;
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var groups = new OrderedDictionary<(string Mode, string Segment), List<string>>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is not (1 or 2) || SegmentOf(r.Exchange) is not { } segment || string.IsNullOrEmpty(r.Token)) continue;
                var instrument = $"{segment}:{r.Token}";
                subs[(r.Symbol, r.Exchange, mode)] = new Sub(r.Symbol, r.Exchange, r.Token, instrument, segment, mode, ModeText(mode));
                var key = (ModeText(mode), segment);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                if (!list.Contains(instrument)) list.Add(instrument);
            }

            var frames = new List<string>();
            foreach (var ((mode, _), instruments) in groups) frames.AddRange(ClientSubscribe(instruments, mode));
            return frames;
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            if (SegmentOf(target.Exchange) is not { } segment) return [];
            var instrument = $"{segment}:{target.Token}";
            var modes = mode is { } given ? [(int)given] : new[] { 1, 2 }.Where(m => subs.ContainsKey((target.Symbol, target.Exchange, m))).ToArray();
            var frames = new List<string>();
            foreach (var m in modes)
            {
                var text = ModeText(m);
                subs.Remove((target.Symbol, target.Exchange, m));
                remembered.Remove($"{target.Symbol}_{target.Exchange}");
                if (client.TryGetValue(text, out var held)) held.Remove(instrument);
                frames.Add(Frame("unsubscribe", text, [instrument]));
                if (subs.Count == 0)
                {
                    // Nothing left: the adapter closes the socket, and the next connection starts from nothing.
                    client.Clear();
                    again = false;
                }
            }

            return frames;
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            var groups = new OrderedDictionary<(string Mode, string Segment), List<string>>();
            foreach (var s in subs.Values)
            {
                if (!groups.TryGetValue((s.ModeText, s.Segment), out var list)) groups[(s.ModeText, s.Segment)] = list = [];
                list.Add(s.Instrument);
            }

            foreach (var ((mode, _), instruments) in groups) frames.AddRange(ClientSubscribe(instruments, mode));
            if (again)
            {
                foreach (var (mode, held) in client)
                {
                    if (held.Count > 0) frames.AddRange(Chunks(held, mode));
                }
            }

            return frames;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            client.Clear();
            subs.Clear();
            remembered.Clear();
            again = false;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    /// <summary>A JSON number as a double, whichever way the parser held it.</summary>
    private static double NumberOf(JsonValue v) => v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<decimal>(out var m) ? (double)m : double.NaN;

    /// <summary>Python truthiness of a JSON value.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => NumberOf(v) != 0,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            _ => false,
        },
        _ => true,
    };

    /// <summary>Python's <c>value == 0</c> for a JSON value: the number zero or false, nothing else.</summary>
    private static bool IsZero(JsonNode? node) => node is JsonValue v && (v.GetValueKind() == JsonValueKind.False || v.GetValueKind() == JsonValueKind.Number && NumberOf(v) == 0);

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs)
    {
        if (frame == "pong") return [];
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(frame);
        }
        catch (JsonException)
        {
            return [];
        }

        if (parsed is not JsonObject message) return [];
        message.TryGetPropertyValue("instrument", out var instrument);
        message.TryGetPropertyValue("mode", out var mode);
        if (!Truthy(instrument) || !Truthy(mode)) return [];
        if (instrument is not JsonValue iv || !iv.TryGetValue<string>(out var instrumentText)) return [];
        var modeText = mode is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;

        lock (gate)
        {
            var sub = subs.Values.FirstOrDefault(s => s.Instrument == instrumentText);
            if (sub is null)
            {
                var byToken = subs.Values.Where(s => s.Token == instrumentText).ToList();
                if (byToken.Count != 1) return [];
                sub = byToken[0];
            }

            var cacheKey = $"{sub.Symbol}_{sub.Exchange}";
            var timestamp = message.TryGetPropertyValue("timestamp", out var stamp) ? stamp?.DeepClone() : JsonValue.Create(nowMs);
            JsonNode? data = message.TryGetPropertyValue("data", out var d) ? d : new JsonObject();
            var cached = remembered.TryGetValue(cacheKey, out var c) ? new Dictionary<string, JsonNode?>(c) : [];
            var result = new JsonObject();
            if (modeText is "ltp" or "quote")
            {
                if (data is not JsonObject fields) return [];
                JsonNode? Value(string key)
                {
                    var found = fields.TryGetPropertyValue(key, out var v);
                    if (found && !IsZero(v)) return v?.DeepClone();
                    return cached.TryGetValue(key, out var old) ? old?.DeepClone() : JsonValue.Create(0);
                }

                result["ltp"] = Value("ltp");
                result["ltt"] = timestamp?.DeepClone();
                if (modeText == "quote")
                {
                    foreach (var key in QuoteFields.Skip(1)) result[key] = Value(key);
                }

                if (!remembered.TryGetValue(cacheKey, out var store)) remembered[cacheKey] = store = [];
                foreach (var (key, value) in result)
                {
                    if (!IsZero(value) && key != "ltt") store[key] = value?.DeepClone();
                }
            }

            result["symbol"] = sub.Symbol;
            result["exchange"] = sub.Exchange;
            result["mode"] = sub.Mode;
            result["timestamp"] = timestamp?.DeepClone();
            return [new FeedPublish($"{sub.Exchange}_{sub.Symbol}_{(modeText == "ltp" ? "LTP" : "QUOTE")}", result)];
        }
    }
}
