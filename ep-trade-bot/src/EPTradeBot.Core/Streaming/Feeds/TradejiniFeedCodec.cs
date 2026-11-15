using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Tradejini's stream (broker/tradejini/streaming and api/nxtradstream.py): one address that carries the token, no sign-in frame. A request names a whole feed (L1 for last price and quote, L5 for depth) and REPLACES the list the server holds for it,
/// so every change sends the complete list of that feed, and a feed with nothing left is cancelled whole. A frame is a header (length, version 1, compression: 100 is zlib) and packets, each a length, a type and key and value fields in the broker's
/// units (prices as integers, a divisor apart by segment). The last price packets of a scrip are merged into one record the next quote is built from; a depth packet carries no prices of the scrip's own, so the depth tick has zeros for them. A packet
/// the stream cannot read ends the frame at that packet, and the ticks before it stand.
/// </summary>
public sealed class TradejiniFeedCodec : IFeedCodec
{
    private readonly TradejiniPacketReader reader = new();
    private sealed record Sub(string Symbol, string Exchange, string Token, string BrokerExchange, int Mode);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];

    public string Broker => "tradejini";

    private static string Feed(int mode) => mode is 1 or 2 ? "L1" : "L5";

    private List<string> Tokens(string feed)
    {
        var tokens = new List<string>();
        foreach (var sub in subs.Values)
        {
            if ((feed == "L1") != (sub.Mode is 1 or 2)) continue;
            var text = $"{sub.Token}_{sub.BrokerExchange}";
            if (!tokens.Contains(text)) tokens.Add(text);
        }

        return tokens;
    }

    /// <summary>The complete list of a feed, or the cancel of it when it is empty and a cancel is wanted.</summary>
    private string? Sync(string feed, bool cancelWhenEmpty)
    {
        var tokens = Tokens(feed);
        if (tokens.Count == 0)
        {
            if (!cancelWhenEmpty) return null;
            if (feed == "L1") reader.ClearL1();
            return new JsonObject { ["type"] = feed, ["action"] = "unsub" }.ToJsonString() + "\n";
        }

        return new JsonObject { ["type"] = feed, ["action"] = "sub", ["tokens"] = new JsonArray(tokens.Select(t => (JsonNode?)new JsonObject { ["t"] = t }).ToArray()) }.ToJsonString() + "\n";
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var dirty = new List<string>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3) continue;
                subs[$"{r.Symbol}_{r.Exchange}_{mode}"] = new Sub(r.Symbol, r.Exchange, r.Token, r.BrokerExchange, mode);
                if (!dirty.Contains(Feed(mode))) dirty.Add(Feed(mode));
            }

            return dirty.Select(f => Sync(f, true)).OfType<string>().ToList();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var dirty = new List<string>();
            foreach (var m in mode is { } one ? [one] : Enum.GetValues<StreamMode>())
            {
                subs.Remove($"{target.Symbol}_{target.Exchange}_{(int)m}");
                if (!dirty.Contains(Feed((int)m))) dirty.Add(Feed((int)m));
            }

            return dirty.Select(f => Sync(f, true)).OfType<string>().ToList();
        }
    }

    /// <summary>A new connection: each feed that holds anything is sent whole. There is nothing to cancel on a connection that is new.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return new[] { "L1", "L5" }.Select(f => Sync(f, false)).OfType<string>().ToList();
    }

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            reader.ClearL1();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var published = new List<FeedPublish>();
        lock (gate)
        {
            foreach (var message in reader.Read(frame)) Publish(message.Data, message.Type, message.Bids, message.Asks, nowMs, published);
        }

        return published;
    }

    // ---- publishing --------------------------------------------------------------------------------------------

    private static JsonNode? Get(Dictionary<string, JsonNode?> data, string key, JsonNode? fallback) => data.TryGetValue(key, out var v) ? v?.DeepClone() : fallback;

    private static JsonNode? Zero => JsonValue.Create(0);

    private static JsonArray Depth(List<Dictionary<string, JsonNode?>> side)
    {
        var rows = new JsonArray();
        foreach (var level in side)
            rows.Add(new JsonObject { ["price"] = Get(level, "price", Zero), ["quantity"] = Get(level, "qty", Zero), ["orders"] = Get(level, "no", Zero) });
        if (rows.Count == 0)
            for (var i = 0; i < 5; i++) rows.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 });
        return rows;
    }

    private void Publish(Dictionary<string, JsonNode?> message, string type, List<Dictionary<string, JsonNode?>> bids, List<Dictionary<string, JsonNode?>> asks, long nowMs, List<FeedPublish> published)
    {
        var symbol = message["symbol"]!.GetValue<string>();
        var parts = symbol.Split('_');
        if (parts.Length != 2) return;
        foreach (var sub in subs.Values.Where(s => s.Token == parts[0] && s.BrokerExchange == parts[1]).ToList())
        {
            var publish = type == "L1" ? sub.Mode is 1 or 2 : sub.Mode == 3;
            if (!publish) continue;
            JsonObject data;
            switch (sub.Mode)
            {
                case 1:
                    data = new JsonObject { ["ltp"] = Get(message, "ltp", Zero), ["ltt"] = Get(message, "ltt", JsonValue.Create("")) };
                    break;
                case 2:
                    data = new JsonObject
                    {
                        ["ltp"] = Get(message, "ltp", Zero), ["ltt"] = Get(message, "ltt", null), ["volume"] = Get(message, "vol", Zero), ["open"] = Get(message, "open", Zero), ["high"] = Get(message, "high", Zero), ["low"] = Get(message, "low", Zero),
                        ["close"] = Get(message, "close", Zero), ["last_quantity"] = Get(message, "ltq", Zero), ["average_price"] = Get(message, "atp", Zero), ["total_buy_quantity"] = Get(message, "totBuyQty", Zero),
                        ["total_sell_quantity"] = Get(message, "totSellQty", Zero), ["oi"] = Get(message, "OI", Zero), ["change"] = Get(message, "chng", Zero), ["change_percent"] = Get(message, "chngPer", Zero),
                    };
                    break;
                default:
                    data = new JsonObject
                    {
                        ["ltp"] = Get(message, "ltp", Zero), ["ltt"] = Get(message, "ltt", null), ["volume"] = Get(message, "vol", Zero), ["open"] = Get(message, "open", Zero), ["high"] = Get(message, "high", Zero), ["low"] = Get(message, "low", Zero),
                        ["close"] = Get(message, "close", Zero), ["oi"] = Get(message, "OI", Zero), ["upper_circuit"] = Get(message, "ucl", Zero), ["lower_circuit"] = Get(message, "lcl", Zero), ["total_buy_quantity"] = Get(message, "totBuyQty", Zero),
                        ["total_sell_quantity"] = Get(message, "totSellQty", Zero), ["depth"] = new JsonObject { ["buy"] = Depth(bids), ["sell"] = Depth(asks) },
                    };
                    break;
            }

            data["symbol"] = sub.Symbol;
            data["exchange"] = sub.Exchange;
            data["mode"] = sub.Mode;
            data["timestamp"] = nowMs;
            published.Add(new FeedPublish($"{sub.Exchange}_{sub.Symbol}_{sub.Mode switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" }}", data));
        }
    }
}
