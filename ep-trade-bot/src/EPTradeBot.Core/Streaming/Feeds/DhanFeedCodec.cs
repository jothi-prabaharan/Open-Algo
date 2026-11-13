using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Dhan's market-data socket, the 5-level connection (broker/dhan/streaming/dhan_websocket.py and dhan_adapter.py). Requests are JSON with a request code per
/// mode (ticker 15, quote 17, full 21, and 16, 18, 22 to stop); frames are little-endian binary packets, each led by an 8-byte header. Dhan tracks one mode per
/// instrument, so asking for another mode replaces the first, and stopping an instrument stops it in the mode it was last asked for. The 20-level
/// connection is not part of this codec: a depth request here is the 5-level full packet. The cap on instruments per connection is the feed host's.
/// </summary>
public sealed class DhanFeedCodec : IFeedCodec
{
    private const int MaxInstrumentsPerRequest = 100;

    private static readonly Dictionary<string, string> Segments = new(StringComparer.Ordinal)
    {
        ["NSE"] = "NSE_EQ", ["BSE"] = "BSE_EQ", ["NFO"] = "NSE_FNO", ["BFO"] = "BSE_FNO", ["MCX"] = "MCX_COMM", ["NCO"] = "NSE_COMM",
        ["CDS"] = "NSE_CURRENCY", ["BCD"] = "BSE_CURRENCY", ["NSE_INDEX"] = "IDX_I", ["BSE_INDEX"] = "IDX_I",
    };

    private static readonly Dictionary<string, int> SegmentCodes = new(StringComparer.Ordinal)
    {
        ["NSE_INDEX"] = 0, ["BSE_INDEX"] = 0, ["NSE"] = 1, ["NFO"] = 2, ["CDS"] = 3, ["BSE"] = 4, ["MCX"] = 5, ["NCO"] = 6, ["BCD"] = 7, ["BFO"] = 8,
    };

    private static readonly Dictionary<string, int> Subscribes = new() { ["TICKER"] = 15, ["QUOTE"] = 17, ["FULL"] = 21 };
    private static readonly Dictionary<string, int> Unsubscribes = new() { ["TICKER"] = 16, ["QUOTE"] = 18, ["FULL"] = 22 };

    private sealed record Sub(string Symbol, string Exchange, string Token, int Mode, string Segment);

    private sealed record Tracked(string Key, string Mode, string Segment, string SecurityId);

    private readonly Lock gate = new();
    private readonly List<(string Corr, Sub Sub)> subs = [];
    private readonly List<Tracked> tracked = [];

    public string Broker => "dhan";

    private static string DhanMode(int mode) => mode switch { 1 => "TICKER", 2 => "QUOTE", _ => "FULL" };

    private static string Request(int code, IReadOnlyList<(string Segment, string SecurityId)> batch) =>
        new JsonObject
        {
            ["RequestCode"] = code,
            ["InstrumentCount"] = batch.Count,
            ["InstrumentList"] = new JsonArray(batch.Select(i => (JsonNode?)new JsonObject { ["ExchangeSegment"] = i.Segment, ["SecurityId"] = i.SecurityId }).ToArray()),
        }.ToJsonString();

    private static IEnumerable<string> Batches(int code, List<(string Segment, string SecurityId)> instruments)
    {
        for (var start = 0; start < instruments.Count; start += MaxInstrumentsPerRequest)
            yield return Request(code, instruments.GetRange(start, Math.Min(MaxInstrumentsPerRequest, instruments.Count - start)));
    }

    private void Track(string mode, string segment, string securityId)
    {
        var key = $"{segment}_{securityId}";
        var at = tracked.FindIndex(t => t.Key == key);
        var entry = new Tracked(key, mode, segment, securityId);
        if (at < 0) tracked.Add(entry);
        else tracked[at] = entry;
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queue = new List<(string Mode, string Segment, string SecurityId)>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3) continue;
                if (!Segments.TryGetValue(r.Exchange, out var segment)) continue;
                var corr = $"{r.Symbol}_{r.Exchange}_{mode}";
                var entry = new Sub(r.Symbol, r.Exchange, r.Token, mode, segment);
                var at = subs.FindIndex(s => s.Corr == corr);
                if (at < 0) subs.Add((corr, entry));
                else subs[at] = (corr, entry);

                queue.Add((DhanMode(mode), segment, r.Token));
            }

            var messages = new List<string>();
            foreach (var group in queue.GroupBy(q => q.Mode))
            {
                var instruments = group.Select(q => (q.Segment, q.SecurityId)).ToList();
                messages.AddRange(Batches(Subscribes[group.Key], instruments));
                foreach (var (segment, id) in instruments) Track(group.Key, segment, id);
            }

            return messages;
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var messages = new List<string>();
            var modes = mode is { } named ? new List<int> { (int)named } : subs.Where(s => s.Sub.Symbol == target.Symbol && s.Sub.Exchange == target.Exchange).Select(s => s.Sub.Mode).ToList();
            foreach (var m in modes)
            {
                if (!Segments.TryGetValue(target.Exchange, out var segment)) continue;
                var corr = $"{target.Symbol}_{target.Exchange}_{m}";
                var at = subs.FindIndex(s => s.Corr == corr);
                if (at < 0) continue;
                subs.RemoveAt(at);

                // Dhan stops the instrument in the mode it is tracked in, whichever mode was asked to stop.
                var key = $"{segment}_{target.Token}";
                var found = tracked.FirstOrDefault(t => t.Key == key);
                if (found is null) continue;
                messages.Add(Request(Unsubscribes[found.Mode], [(segment, target.Token)]));
                tracked.Remove(found);
            }

            return messages;
        }
    }

    /// <summary>Every tracked instrument again, by mode. OpenAlgo sends the same requests a second time from its own list; Dhan gains nothing from the repeat.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            var messages = new List<string>();
            foreach (var group in tracked.GroupBy(t => t.Mode))
                messages.AddRange(Batches(Subscribes[group.Key], group.Select(t => (t.Segment, t.SecurityId)).ToList()));
            return messages;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            tracked.Clear();
        }
    }

    // ---- reading what comes back -----------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var published = new List<FeedPublish>();
        var offset = 0;
        while (offset < frame.Length)
        {
            if (offset + 8 > frame.Length) break;
            var code = frame[offset];
            var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset + 1, 2));
            var segment = frame[offset + 3];
            var securityId = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset + 4, 4));
            var end = offset + length;
            if (end > frame.Length) break;
            // A packet that claims to end before it begins would never move on.
            if (end <= offset) break;
            var payload = end >= offset + 8 ? frame[(offset + 8)..end] : [];

            var data = code switch
            {
                2 => Ticker(payload, segment, securityId),
                4 => Quote(payload, segment, securityId),
                8 => Full(payload, segment, securityId),
                _ => null,
            };
            if (data is not null) Publish(data, nowMs, published);
            offset = end;
        }

        return published;
    }

    private static JsonObject? Ticker(ReadOnlySpan<byte> p, int segment, uint id)
    {
        if (p.Length < 8) return null;
        return new JsonObject { ["type"] = "ticker", ["exchange_segment"] = segment, ["security_id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["ltp"] = Single(p, 0), ["ltt"] = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]) };
    }

    private static JsonObject? Quote(ReadOnlySpan<byte> p, int segment, uint id)
    {
        if (p.Length < 42) return null;
        return new JsonObject
        {
            ["type"] = "quote", ["exchange_segment"] = segment, ["security_id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ltp"] = Single(p, 0), ["ltq"] = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]), ["ltt"] = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]),
            ["atp"] = Single(p, 10), ["volume"] = BinaryPrimitives.ReadUInt32LittleEndian(p[14..]),
            ["total_sell_quantity"] = BinaryPrimitives.ReadUInt32LittleEndian(p[18..]), ["total_buy_quantity"] = BinaryPrimitives.ReadUInt32LittleEndian(p[22..]),
            ["open"] = Single(p, 26), ["close"] = Single(p, 30), ["high"] = Single(p, 34), ["low"] = Single(p, 38),
        };
    }

    private static JsonObject? Full(ReadOnlySpan<byte> p, int segment, uint id)
    {
        if (p.Length < 154) return null;
        var buy = new JsonArray();
        var sell = new JsonArray();
        for (var i = 0; i < 5; i++)
        {
            var at = 54 + i * 20;
            buy.Add(new JsonObject { ["price"] = Single(p, at + 12), ["quantity"] = BinaryPrimitives.ReadUInt32LittleEndian(p[at..]), ["orders"] = BinaryPrimitives.ReadUInt16LittleEndian(p[(at + 8)..]) });
            sell.Add(new JsonObject { ["price"] = Single(p, at + 16), ["quantity"] = BinaryPrimitives.ReadUInt32LittleEndian(p[(at + 4)..]), ["orders"] = BinaryPrimitives.ReadUInt16LittleEndian(p[(at + 10)..]) });
        }

        return new JsonObject
        {
            ["type"] = "full", ["exchange_segment"] = segment, ["security_id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ltp"] = Single(p, 0), ["ltq"] = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]), ["ltt"] = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]),
            ["atp"] = Single(p, 10), ["volume"] = BinaryPrimitives.ReadUInt32LittleEndian(p[14..]),
            ["total_sell_quantity"] = BinaryPrimitives.ReadUInt32LittleEndian(p[18..]), ["total_buy_quantity"] = BinaryPrimitives.ReadUInt32LittleEndian(p[22..]),
            ["oi"] = BinaryPrimitives.ReadUInt32LittleEndian(p[26..]), ["oi_high"] = BinaryPrimitives.ReadUInt32LittleEndian(p[30..]), ["oi_low"] = BinaryPrimitives.ReadUInt32LittleEndian(p[34..]),
            ["open"] = Single(p, 38), ["close"] = Single(p, 42), ["high"] = Single(p, 46), ["low"] = Single(p, 50),
            ["depth"] = new JsonObject { ["buy"] = buy, ["sell"] = sell },
        };
    }

    /// <summary>A 32-bit float, widened to the double OpenAlgo publishes (so 805.45 reads as 805.4500122070312).</summary>
    private static double Single(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadSingleLittleEndian(p[at..]);

    private void Publish(JsonObject data, long nowMs, List<FeedPublish> published)
    {
        var securityId = (string)data["security_id"]!;
        var segment = (int)data["exchange_segment"]!;
        Sub? match;
        lock (gate)
        {
            // The security id and the segment together; failing that, the security id alone.
            match = subs.Select(s => s.Sub).FirstOrDefault(s => s.Token == securityId && SegmentCodes.GetValueOrDefault(s.Exchange, -1) == segment)
                ?? subs.Select(s => s.Sub).FirstOrDefault(s => s.Token == securityId);
        }

        if (match is null) return;
        var type = (string)data["type"]!;
        var tick = new JsonObject { ["symbol"] = match.Symbol, ["exchange"] = match.Exchange, ["timestamp"] = nowMs };
        string suffix;
        switch (type)
        {
            case "ticker":
                suffix = "LTP";
                tick["mode"] = 1;
                tick["ltp"] = data["ltp"]!.DeepClone();
                tick["ltt"] = data["ltt"]!.DeepClone();
                break;
            case "quote":
                suffix = "QUOTE";
                tick["mode"] = 2;
                foreach (var (name, source) in new[] { ("ltp", "ltp"), ("ltt", "ltt"), ("volume", "volume"), ("open", "open"), ("high", "high"), ("low", "low"), ("close", "close"),
                    ("last_quantity", "ltq"), ("average_price", "atp"), ("total_buy_quantity", "total_buy_quantity"), ("total_sell_quantity", "total_sell_quantity") })
                    tick[name] = data[source]!.DeepClone();
                break;
            case "full":
                suffix = "DEPTH";
                tick["mode"] = 3;
                foreach (var name in new[] { "ltp", "ltt", "volume", "open", "high", "low", "close", "oi", "oi_high", "oi_low", "depth" }) tick[name] = data[name]!.DeepClone();
                tick["depth_level"] = 5;
                break;
            default:
                return;
        }

        published.Add(new FeedPublish($"{match.Exchange}_{match.Symbol}_{suffix}", tick));
    }
}
