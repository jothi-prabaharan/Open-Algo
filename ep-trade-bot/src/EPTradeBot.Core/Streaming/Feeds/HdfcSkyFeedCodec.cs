using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// HDFC Sky's stream (broker/hdfcsky/streaming): the same JSON frames and GenericDTO packets as HDFC Securities, with simpler state. A token has one mode (the last asked for), the packet type does not
/// name the exchange so a token is matched alone, circuit and open-interest packets are read as the quotes they are shaped like (their price fields are zero), and a connection holds 300 scrips.
/// An unsubscribe drops the token whatever mode is named, with the type last sent for it.
/// </summary>
public sealed class HdfcSkyFeedCodec : IFeedCodec
{
    private const int MaxInstruments = 300;
    private const int MaxPerFrame = 100;

    private readonly Lock gate = new();
    private readonly Dictionary<long, (string Symbol, string Exchange, StreamMode Mode)> info = [];
    private readonly OrderedDictionary<string, string> subscribed = [];
    private readonly List<(string Scrip, string Type)> pending = [];

    public string Broker => "hdfcsky";

    private static string Prefix(string exchange) => exchange == "CDS" ? "NCD" : exchange;

    private static string TypeOf(StreamMode mode) => mode == StreamMode.Ltp ? "LTP" : "ALL";

    private static string Message(string key, IEnumerable<(string Scrip, string Type)> items) =>
        new JsonObject
        {
            ["heart_beat"] = false,
            [key] = new JsonArray(items.Select(i => (JsonNode?)new JsonObject { ["scripId"] = i.Scrip, ["type"] = i.Type }).ToArray()),
        }.ToJsonString();

    private static IEnumerable<string> Frames(IEnumerable<(string Scrip, string Type)> items)
    {
        var byType = new List<(string Type, List<string> Scrips)>();
        foreach (var (scrip, type) in items)
        {
            var group = byType.Find(g => g.Type == type);
            if (group.Scrips is null)
            {
                group = (type, []);
                byType.Add(group);
            }

            group.Scrips.Add(scrip);
        }

        foreach (var (type, scrips) in byType)
        {
            foreach (var chunk in scrips.Chunk(MaxPerFrame)) yield return Message("subscribe", chunk.Select(s => (s, type)));
        }
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            foreach (var r in requests)
            {
                if (!long.TryParse(r.Token, out var token)) continue;
                info[token] = (r.Symbol, r.Exchange, r.Mode);

                // The limit counts what has been sent, not what is queued in this call.
                if (subscribed.Count + 1 > MaxInstruments) continue;
                pending.Add(($"{Prefix(r.Exchange)}_{token}", TypeOf(r.Mode)));
            }

            var frames = new List<string>();
            foreach (var group in pending.GroupBy(p => p.Type))
            {
                // Grouped by type in the order each type first appeared, a hundred to a frame.
                foreach (var chunk in group.Select(p => p.Scrip).Chunk(MaxPerFrame))
                {
                    frames.Add(Message("subscribe", chunk.Select(s => (s, group.Key))));
                    foreach (var s in chunk) subscribed[s] = group.Key;
                }
            }

            pending.Clear();
            return frames;
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            if (!long.TryParse(target.Token, out var token)) return [];
            var scrip = $"{Prefix(target.Exchange)}_{token}";
            var frame = Message("unSubscribe", [(scrip, subscribed.GetValueOrDefault(scrip) ?? "ALL")]);
            subscribed.Remove(scrip);
            info.Remove(token);
            return [frame];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return Frames(subscribed.Select(s => (s.Key, s.Value))).ToList();
    }

    public void Reset()
    {
        lock (gate)
        {
            info.Clear();
            subscribed.Clear();
            pending.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

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
            lock (gate)
            {
                var token = tick["token"]!.GetValue<long>();
                if (!info.TryGetValue(token, out var held)) continue;
                var topic = held.Mode switch { StreamMode.Ltp => StreamMode.Ltp, StreamMode.Depth => StreamMode.Depth, _ => StreamMode.Quote };
                published.Add(new FeedPublish(MarketTopics.Format(held.Exchange, held.Symbol, topic), GenericDtoFeedCodec.Normalize(tick, token, held.Symbol, held.Exchange, topic)));
            }
        }

        return published;
    }

    private static JsonObject? ToTick(GenericDto p, long nowMs)
    {
        if (p.PacketType == 16) return null;
        if (p.InstrumentId == 0) return null;
        var tick = new JsonObject { ["token"] = p.InstrumentId, ["timestamp"] = p.PacketTimestamp != 0 ? p.PacketTimestamp : nowMs };
        switch (p.PacketType)
        {
            case 2 or 5:
                GenericDtoFeedCodec.FillIndex(tick, p);
                return tick;
            case 17 or 18:
                return null;
        }

        if (p.PacketType is 0 or 1 or 3 or 4 or 6 or 7 or 10 or 11 or 12 or 13 or 14 or 15 || p.Mbp is not null)
        {
            GenericDtoFeedCodec.FillMbp(tick, p);
            return tick;
        }

        return null;
    }
}
