using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Dhan's 20-level depth socket (broker/dhan streaming dhan_websocket.py with is_20_depth, and dhan_adapter.py). Asked for by a subscriber who adds :20 to the symbol, on NSE and NFO only and in depth mode. Requests are JSON (code 23 to start, 24 to stop,
/// the instruments fifty to a request); each message is led by a 12-byte header (length, response code, exchange segment, security id) and the bid side (code 41) and the ask side (code 51) each carry twenty 16-byte levels. A tick is published, to the topic of
/// the symbol with its :20, once both sides of a security have arrived. OpenAlgo's adapter cannot stop such a subscription (it asks for the token of the symbol with its :20, which does not exist), so the stop request here is the one its client would send.
/// </summary>
public sealed class DhanDepth20FeedCodec : IFeedCodec
{
    private const int MaxPerRequest = 50;
    private const int HeaderSize = 12;
    private const int LevelCount = 20;

    private static readonly Dictionary<string, (string Segment, int Code)> Segments = new(StringComparer.Ordinal) { ["NSE"] = ("NSE_EQ", 1), ["NFO"] = ("NSE_FNO", 2) };

    private sealed record Sub(string Symbol, string Exchange, string Token, string Segment, int SegmentCode);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly Dictionary<string, (JsonArray? Buy, JsonArray? Sell)> accumulator = [];

    public string Broker => "dhan";

    private static string Request(int code, IReadOnlyList<Sub> batch) =>
        new JsonObject
        {
            ["RequestCode"] = code,
            ["InstrumentCount"] = batch.Count,
            ["InstrumentList"] = new JsonArray(batch.Select(s => (JsonNode?)new JsonObject { ["ExchangeSegment"] = s.Segment, ["SecurityId"] = s.Token }).ToArray()),
        }.ToJsonString();

    private static IEnumerable<string> Batches(int code, IReadOnlyList<Sub> items)
    {
        for (var start = 0; start < items.Count; start += MaxPerRequest)
            yield return Request(code, items.Skip(start).Take(MaxPerRequest).ToList());
    }

    private static string KeyOf(string symbol, string exchange) => $"{symbol}_{exchange}_3_20";

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queued = new List<Sub>();
            foreach (var r in requests)
            {
                if (r.Mode != StreamMode.Depth || !Segments.TryGetValue(r.Exchange, out var segment) || string.IsNullOrEmpty(r.Token)) continue;
                var sub = new Sub(r.Symbol, r.Exchange, r.Token, segment.Segment, segment.Code);
                subs[KeyOf(r.Symbol, r.Exchange)] = sub;
                queued.Add(sub);
            }

            return Batches(23, queued).ToList();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            if (mode is { } named && named != StreamMode.Depth) return [];
            if (!subs.Remove(KeyOf(target.Symbol, target.Exchange), out var sub)) return [];
            return [Request(24, [sub])];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return Batches(23, subs.Values.ToList()).ToList();
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            accumulator.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var published = new List<FeedPublish>();
        lock (gate)
        {
            var offset = 0;
            while (offset < frame.Length)
            {
                if (offset + HeaderSize > frame.Length) break;
                var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset, 2));
                var code = frame[offset + 2];
                var segment = frame[offset + 3];
                var securityId = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset + 4, 4));
                var payloadEnd = offset + length;
                if (length < HeaderSize || payloadEnd > frame.Length) break;
                if (code is 41 or 51 && Parse(frame[(offset + HeaderSize)..payloadEnd]) is { } levels)
                    Accumulate(securityId.ToString(System.Globalization.CultureInfo.InvariantCulture), segment, code == 41, levels, nowMs, published);
                offset = payloadEnd;
            }
        }

        return published;
    }

    private static JsonArray? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < LevelCount * 16) return null;
        var levels = new JsonArray();
        for (var i = 0; i < LevelCount; i++)
        {
            var at = i * 16;
            levels.Add(new JsonObject
            {
                ["price"] = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(at, 8)), ["quantity"] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(at + 8, 4)), ["orders"] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(at + 12, 4)),
            });
        }

        return levels;
    }

    private void Accumulate(string securityId, int segment, bool isBid, JsonArray levels, long nowMs, List<FeedPublish> published)
    {
        var held = accumulator.TryGetValue(securityId, out var h) ? h : (null, null);
        held = isBid ? (levels, held.Sell) : (held.Buy, levels);
        accumulator[securityId] = held;
        if (held.Buy is null || held.Sell is null) return;
        var sub = subs.Values.FirstOrDefault(s => s.Token == securityId && s.SegmentCode == segment);
        if (sub is null)
        {
            accumulator.Remove(securityId);
            return;
        }

        published.Add(new FeedPublish($"{sub.Exchange}_{sub.Symbol}_DEPTH", new JsonObject
        {
            ["symbol"] = sub.Symbol, ["exchange"] = sub.Exchange, ["mode"] = 3, ["timestamp"] = nowMs, ["depth"] = new JsonObject { ["buy"] = held.Buy, ["sell"] = held.Sell }, ["depth_level"] = 20,
        }));
        accumulator.Remove(securityId);
    }
}
