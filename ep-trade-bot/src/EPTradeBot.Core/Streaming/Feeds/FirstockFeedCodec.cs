using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Firstock's stream (broker/firstock/streaming): JSON over a WebSocket. The server answers a new connection with a status frame and nothing is sent before it says success: the scrips wait in a queue and go out
/// in one subscribe frame (EXCHANGE:TOKEN joined by a bar, grouped by exchange) when it does. A scrip is on the wire once however many modes want it, and comes off it with the last of them. The mode is never sent: a tick carries the full
/// picture, and goes to every subscription on its scrip. A tick arrives with its fields at the top level or wrapped under its EXCHANGE:TOKEN key, and the snapshot of a scrip (shared by its modes) holds the last valid value of
/// every field, a price that is zero and a number that is the invalid marker not being valid.
/// </summary>
public sealed class FirstockFeedCodec : IFeedCodec
{
    private const double InvalidMarker = 9223372036854775808.0;
    private const ulong InvalidMarkerExact = 9223372036854775808UL;

    private static readonly string[] PriceFields =
    [
        "i_last_traded_price", "i_open_price", "i_high_price", "i_low_price", "i_closing_price", "i_average_trade_price", "i_upper_circuit_limit", "i_lower_circuit_limit",
    ];

    private static readonly string[] MergeFields =
    [
        .. PriceFields, "i_volume_traded_today", "i_last_trade_quantity", "i_total_buy_quantity", "i_total_sell_quantity", "i_open_interest", "i_total_open_interest", "i_last_trade_time", "i_feed_time", "c_exch_feed_time",
    ];

    private sealed record Sub(string Symbol, string Exchange, StreamMode Mode, string Token, string BrokerExchange)
    {
        public string Scrip => $"{BrokerExchange}:{Token}";
    }

    private sealed class Snapshot
    {
        public Dictionary<string, JsonNode?> Fields { get; } = [];

        public List<JsonObject>? Buy { get; set; }

        public List<JsonObject>? Sell { get; set; }
    }

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly List<(string Exchange, string Token)> queue = [];
    private readonly Dictionary<string, Snapshot> snapshots = [];
    private readonly List<string> outgoing = [];
    private bool authenticated;
    private bool failed;

    public string Broker => "firstock";

    private static string Key(string symbol, string exchange, StreamMode mode) => $"{symbol}_{exchange}_{(int)mode}";

    /// <summary>A new connection: the server has not accepted it yet.</summary>
    public void Begin(EPTradeBot.Core.Brokers.BrokerSession session)
    {
        lock (gate)
        {
            authenticated = false;
            failed = false;
        }
    }

    private void Enqueue(string exchange, string token)
    {
        if (!queue.Any(q => q.Exchange == exchange && q.Token == token)) queue.Add((exchange, token));
    }

    private bool OnWire(string scrip) => subs.Values.Any(s => s.Scrip == scrip);

    /// <summary>The waiting scrips as one frame, once the server has accepted the connection. Nothing goes after a refused sign-in.</summary>
    private List<string> Flush()
    {
        var frames = new List<string>();
        if (!authenticated || failed) return frames;
        var items = queue.Where(q => OnWire($"{q.Exchange}:{q.Token}")).ToList();
        queue.Clear();
        if (items.Count == 0) return frames;
        var tokens = items.GroupBy(i => i.Exchange).SelectMany(g => g.Select(i => $"{g.Key}:{i.Token}"));
        frames.Add(new JsonObject { ["action"] = "subscribe", ["tokens"] = string.Join('|', tokens) }.ToJsonString());
        return frames;
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            foreach (var r in requests)
            {
                var key = Key(r.Symbol, r.Exchange, r.Mode);
                if (subs.ContainsKey(key)) continue;
                var sub = new Sub(r.Symbol, r.Exchange, r.Mode, r.Token, r.BrokerExchange);
                var first = !OnWire(sub.Scrip);
                subs[key] = sub;
                if (first) Enqueue(sub.BrokerExchange, sub.Token);
            }

            return Flush();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var scrip = $"{target.BrokerExchange}:{target.Token}";
            var removed = false;
            foreach (var m in mode is { } one ? [one] : Enum.GetValues<StreamMode>())
            {
                removed |= subs.Remove(Key(target.Symbol, target.Exchange, m));
            }

            if (!removed || OnWire(scrip)) return [];
            snapshots.Remove(scrip);
            // A scrip that never left the queue was never sent, so there is nothing to take back.
            if (queue.RemoveAll(q => q.Exchange == target.BrokerExchange && q.Token == target.Token) > 0 || failed) return [];
            return [new JsonObject { ["action"] = "unsubscribe", ["tokens"] = scrip }.ToJsonString()];
        }
    }

    /// <summary>A new connection has to be accepted again before anything is sent, so every scrip goes back in the queue and waits for the answer.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            authenticated = false;
            failed = false;
            foreach (var sub in subs.Values) Enqueue(sub.BrokerExchange, sub.Token);
            return Flush();
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
            subs.Clear();
            queue.Clear();
            snapshots.Clear();
            outgoing.Clear();
            authenticated = false;
            failed = false;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    // ---- reading -----------------------------------------------------------------------------------------------

    private static string? Text(JsonObject message, string name) => message.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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
            if (data.ContainsKey("status"))
            {
                if (Text(data, "status") == "success")
                {
                    authenticated = true;
                    outgoing.AddRange(Flush());
                }
                else if (Text(data, "status") == "failed" || Text(data, "message") == "unauthenticated")
                {
                    authenticated = false;
                    failed = true;
                }

                return [];
            }

            if (data.ContainsKey("c_symbol")) return OnTick(data, nowMs);
            var published = new List<FeedPublish>();
            foreach (var (key, value) in data.ToList())
            {
                if (!key.Contains(':') || value is not JsonObject tick) continue;
                var colon = key.IndexOf(':');
                if (!tick.ContainsKey("c_symbol")) tick["c_symbol"] = key[(colon + 1)..];
                if (!tick.ContainsKey("c_exch_seg")) tick["c_exch_seg"] = key[..colon];
                published.AddRange(OnTick(tick, nowMs));
            }

            return published;
        }
    }

    private List<FeedPublish> OnTick(JsonObject data, long nowMs)
    {
        var published = new List<FeedPublish>();
        var token = Text(data, "c_symbol");
        var exchange = Text(data, "c_exch_seg");
        if (token is null || exchange is null) return published;
        var matching = subs.Values.Where(s => s.Token == token && s.BrokerExchange == exchange).ToList();
        if (matching.Count == 0) return published;
        var snapshot = Update(matching[0].Scrip, data);
        try
        {
            foreach (var sub in matching)
            {
                var result = Normalize(snapshot, sub.Mode);
                result["symbol"] = sub.Symbol;
                result["exchange"] = sub.Exchange;
                result["mode"] = (int)sub.Mode;
                result["timestamp"] = nowMs;
                published.Add(new FeedPublish(MarketTopics.Format(sub.Exchange, sub.Symbol, sub.Mode), result));
            }
        }
        catch (FormatException)
        {
            // A price that is not a number stops the frame where it is, the subscriptions before it having gone out.
        }

        return published;
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return v.TryGetValue<bool>(out var b) ? b ? 1 : 0 : null;
    }

    private static bool IsInvalid(JsonNode node)
    {
        if (node is JsonValue v && v.TryGetValue<ulong>(out var exact)) return exact == InvalidMarkerExact;
        return Number(node) is { } d && d == InvalidMarker;
    }

    private Snapshot Update(string scrip, JsonObject data)
    {
        if (!snapshots.TryGetValue(scrip, out var snapshot)) snapshots[scrip] = snapshot = new Snapshot();
        foreach (var field in MergeFields)
        {
            if (!data.TryGetPropertyValue(field, out var value)) continue;
            if (value is not null && Number(value) is { } number)
            {
                if (IsInvalid(value) || (number == 0 && PriceFields.Contains(field))) continue;
            }

            snapshot.Fields[field] = value?.DeepClone();
        }

        if (data.ContainsKey("best_buy") && data.ContainsKey("best_sell"))
        {
            var buy = Levels(data["best_buy"]);
            var sell = Levels(data["best_sell"]);
            if (buy.Count > 0) snapshot.Buy = buy;
            else snapshot.Buy ??= [];
            if (sell.Count > 0) snapshot.Sell = sell;
            else snapshot.Sell ??= [];
        }

        return snapshot;
    }

    private static double Level(JsonObject level, string name) => level.TryGetPropertyValue(name, out var node) && Number(node) is { } d ? d : 0;

    /// <summary>The levels that carry something: not the invalid marker in any field, and a price or a quantity above zero.</summary>
    private static List<JsonObject> Levels(JsonNode? list)
    {
        var kept = new List<JsonObject>();
        if (list is not JsonArray array) return kept;
        foreach (var level in array.OfType<JsonObject>())
        {
            var invalid = new[] { "price", "quantity", "orders" }.Any(n => level.TryGetPropertyValue(n, out var node) && node is not null && IsInvalid(node));
            if (!invalid && (Level(level, "price") > 0 || Level(level, "quantity") > 0)) kept.Add((JsonObject)level.DeepClone());
        }

        return kept;
    }

    /// <summary>Python's float() on what the snapshot holds: a number, or text that reads as one. Anything else cannot be converted.</summary>
    private static double ToDouble(JsonNode? node)
    {
        if (Number(node) is { } d) return d;
        if (node is JsonValue v && v.TryGetValue<string>(out var text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new FormatException("Not a number.");
    }

    private static JsonNode? Field(Snapshot snapshot, string name) => snapshot.Fields.TryGetValue(name, out var value) ? value?.DeepClone() : JsonValue.Create(0);

    private static JsonNode? Rupees(Snapshot snapshot, string name) => JsonValue.Create((snapshot.Fields.TryGetValue(name, out var value) ? ToDouble(value) : 0.0) / 100.0);

    private static JsonObject Normalize(Snapshot snapshot, StreamMode mode)
    {
        if (mode == StreamMode.Ltp) return new JsonObject { ["ltp"] = Rupees(snapshot, "i_last_traded_price"), ["ltt"] = Field(snapshot, "i_last_trade_time") };
        if (mode == StreamMode.Quote)
        {
            return new JsonObject
            {
                ["ltp"] = Rupees(snapshot, "i_last_traded_price"), ["ltt"] = Field(snapshot, "i_last_trade_time"), ["volume"] = Field(snapshot, "i_volume_traded_today"), ["open"] = Rupees(snapshot, "i_open_price"),
                ["high"] = Rupees(snapshot, "i_high_price"), ["low"] = Rupees(snapshot, "i_low_price"), ["close"] = Rupees(snapshot, "i_closing_price"), ["last_quantity"] = Field(snapshot, "i_last_trade_quantity"),
                ["average_price"] = Rupees(snapshot, "i_average_trade_price"), ["total_buy_quantity"] = Field(snapshot, "i_total_buy_quantity"), ["total_sell_quantity"] = Field(snapshot, "i_total_sell_quantity"),
                ["oi"] = Field(snapshot, "i_open_interest"), ["upper_circuit"] = Rupees(snapshot, "i_upper_circuit_limit"), ["lower_circuit"] = Rupees(snapshot, "i_lower_circuit_limit"),
            };
        }

        return new JsonObject
        {
            ["ltp"] = Rupees(snapshot, "i_last_traded_price"), ["ltt"] = Field(snapshot, "i_last_trade_time"), ["volume"] = Field(snapshot, "i_volume_traded_today"), ["open"] = Rupees(snapshot, "i_open_price"),
            ["high"] = Rupees(snapshot, "i_high_price"), ["low"] = Rupees(snapshot, "i_low_price"), ["close"] = Rupees(snapshot, "i_closing_price"), ["oi"] = Field(snapshot, "i_total_open_interest"),
            ["upper_circuit"] = Rupees(snapshot, "i_upper_circuit_limit"), ["lower_circuit"] = Rupees(snapshot, "i_lower_circuit_limit"),
            ["depth"] = new JsonObject { ["buy"] = Ladder(snapshot.Buy), ["sell"] = Ladder(snapshot.Sell) },
        };
    }

    /// <summary>Five levels, a shorter ladder padded with empty ones and a longer one cut.</summary>
    private static JsonArray Ladder(List<JsonObject>? levels)
    {
        var ladder = new JsonArray();
        foreach (var level in (levels ?? []).Take(5))
        {
            ladder.Add(new JsonObject
            {
                ["price"] = Level(level, "price") / 100.0,
                ["quantity"] = level.TryGetPropertyValue("quantity", out var q) ? q?.DeepClone() : JsonValue.Create(0),
                ["orders"] = level.TryGetPropertyValue("orders", out var o) ? o?.DeepClone() : JsonValue.Create(0),
            });
        }

        while (ladder.Count < 5) ladder.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 });
        return ladder;
    }
}
