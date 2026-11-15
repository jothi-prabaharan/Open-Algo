using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Definedge's stream (broker/definedge/streaming): JSON over a WebSocket. A connection is signed in by a connect frame (user id, account id, the source TRTP and the session token) and the server answers it with a ck frame; nothing is
/// asked for before that answer. Touchline and depth are separate subscriptions, scrips joined by a hash. A scrip is on the wire once per kind however many modes want it (the quote and last price modes share the touchline): the first of a
/// kind sends it, the last one to go takes it back. A new connection is signed in again and then every subscription is repeated. Ticks are merged over the last ones of the scrip (a zero open, high, low, close or average never replaces a
/// value already held) and published for the first subscription found on the scrip, whatever its mode, so a second mode on a scrip gets no touchline ticks of its own. A depth tick also publishes the quote and last price modes of the scrip.
/// </summary>
public sealed class DefinedgeFeedCodec : IFeedCodec
{
    private static readonly string[] KeepNonZero = ["o", "h", "l", "c", "ap"];

    private sealed record Sub(string Symbol, string Exchange, StreamMode Mode, string Token, string BrokerExchange)
    {
        public string Scrip => $"{BrokerExchange}|{Token}";
    }

    private sealed class Refs
    {
        public int Tick { get; set; }

        public int Depth { get; set; }
    }

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly Dictionary<string, Refs> refs = [];
    private readonly Dictionary<string, Dictionary<string, JsonNode?>> cache = [];
    private readonly List<(string Exchange, string Token, bool Depth)> queue = [];
    private readonly List<string> outgoing = [];
    private string userId = "";
    private string userToken = "";
    private bool connected;
    private bool authenticated;
    private bool resubscribePending;

    public string Broker => "definedge";

    private static string Key(string symbol, string exchange, StreamMode mode) => $"{symbol}_{exchange}_{(int)mode}";

    private static string Exchange(string brokerExchange) => brokerExchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => brokerExchange };

    private static bool IsTouchline(StreamMode mode) => mode is StreamMode.Ltp or StreamMode.Quote;

    /// <summary>A new connection attempt: the user id is the account's client id and the session token is the second part of the stored session.</summary>
    public void Begin(EPTradeBot.Core.Brokers.BrokerSession session)
    {
        lock (gate)
        {
            var parts = session.AuthToken.Split(":::");
            userId = session.ClientId ?? "";
            userToken = !string.IsNullOrEmpty(session.FeedToken) ? session.FeedToken : parts.Length > 1 ? parts[1] : "";
            connected = false;
            authenticated = false;
            resubscribePending = false;
        }
    }

    private static string Frame(string type, IEnumerable<(string Exchange, string Token)> scrips) =>
        new JsonObject { ["t"] = type, ["k"] = string.Join('#', scrips.Select(s => $"{s.Exchange}|{s.Token}")) }.ToJsonString();

    // ---- sending -----------------------------------------------------------------------------------------------

    /// <summary>The queued scrips, one touchline frame and then one depth frame, each scrip once. Only a connection that has been signed in sends them; for any other they are lost, and the repeat after the next sign-in is what restores them.</summary>
    private List<string> Flush()
    {
        var frames = new List<string>();
        var ticks = queue.Where(q => !q.Depth).Select(q => (q.Exchange, q.Token)).Distinct().ToList();
        var depths = queue.Where(q => q.Depth).Select(q => (q.Exchange, q.Token)).Distinct().ToList();
        queue.Clear();
        if (!connected || !authenticated) return frames;
        if (ticks.Count > 0) frames.Add(Frame("t", ticks));
        if (depths.Count > 0) frames.Add(Frame("d", depths));
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
                var sub = new Sub(r.Symbol, r.Exchange, r.Mode, r.Token, Exchange(r.BrokerExchange));
                subs[key] = sub;
                if (!connected) continue;
                if (!refs.TryGetValue(sub.Scrip, out var count)) refs[sub.Scrip] = count = new Refs();
                if (IsTouchline(sub.Mode))
                {
                    if (count.Tick++ == 0) queue.Add((sub.BrokerExchange, sub.Token, false));
                }
                else if (count.Depth++ == 0)
                {
                    queue.Add((sub.BrokerExchange, sub.Token, true));
                }
            }

            return Flush();
        }
    }

    /// <summary>Takes one reference off the scrip for this kind; true when it was the last, and the broker is to be told.</summary>
    private bool Release(string scrip, StreamMode mode)
    {
        if (!refs.TryGetValue(scrip, out var count)) return true;
        if (IsTouchline(mode))
        {
            if (--count.Tick > 0) return false;
            count.Tick = 0;
            if (count.Depth == 0) refs.Remove(scrip);
            return true;
        }

        if (--count.Depth > 0) return false;
        count.Depth = 0;
        if (count.Tick == 0) refs.Remove(scrip);
        return true;
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var frames = new List<string>();
            foreach (var m in mode is { } one ? [one] : Enum.GetValues<StreamMode>())
            {
                if (!subs.Remove(Key(target.Symbol, target.Exchange, m), out var sub)) continue;
                if (!subs.Values.Any(s => s.Scrip == sub.Scrip)) cache.Remove(sub.Scrip);
                if (Release(sub.Scrip, m) && connected && authenticated) frames.Add(Frame(IsTouchline(m) ? "u" : "ud", [(sub.BrokerExchange, sub.Token)]));
            }

            return frames;
        }
    }

    private string? ConnectFrame() => userId.Length == 0 || userToken.Length == 0
        ? null
        : new JsonObject { ["t"] = "c", ["uid"] = userId, ["actid"] = userId, ["source"] = "TRTP", ["susertoken"] = userToken }.ToJsonString();

    /// <summary>A new connection: signed in again first. The subscriptions are repeated when the server accepts it.</summary>
    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            connected = true;
            authenticated = false;
            queue.Clear();
            resubscribePending = subs.Count > 0;
            return ConnectFrame() is { } frame ? [frame] : [];
        }
    }

    /// <summary>Every subscription again, with the counts rebuilt from them: one touchline frame, then one depth frame.</summary>
    private List<string> RepeatAll()
    {
        refs.Clear();
        var ticks = new List<(string Exchange, string Token)>();
        var depths = new List<(string Exchange, string Token)>();
        foreach (var sub in subs.Values)
        {
            if (!refs.TryGetValue(sub.Scrip, out var count)) refs[sub.Scrip] = count = new Refs();
            if (IsTouchline(sub.Mode))
            {
                if (!ticks.Contains((sub.BrokerExchange, sub.Token))) ticks.Add((sub.BrokerExchange, sub.Token));
                count.Tick++;
            }
            else
            {
                if (!depths.Contains((sub.BrokerExchange, sub.Token))) depths.Add((sub.BrokerExchange, sub.Token));
                count.Depth++;
            }
        }

        var frames = new List<string>();
        if (ticks.Count > 0) frames.Add(Frame("t", ticks));
        if (depths.Count > 0) frames.Add(Frame("d", depths));
        return frames;
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
            refs.Clear();
            cache.Clear();
            queue.Clear();
            outgoing.Clear();
            connected = false;
            authenticated = false;
            resubscribePending = false;
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
            try
            {
                switch (Text(data, "t"))
                {
                    case "ck":
                        if (Text(data, "s") is "Ok" or "OK")
                        {
                            authenticated = true;
                            if (resubscribePending)
                            {
                                resubscribePending = false;
                                outgoing.AddRange(RepeatAll());
                            }
                        }

                        return [];
                    case "tk" or "tf":
                        return OnTouchline(data, nowMs);
                    case "dk" or "df":
                        return OnDepth(data, nowMs);
                    default:
                        return [];
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                // A number the feed cannot read stops the message where it is.
                return [];
            }
        }
    }

    private Sub? Find(JsonObject data)
    {
        var token = Text(data, "tk");
        var exchange = Text(data, "e");
        return token is null || exchange is null ? null : subs.Values.FirstOrDefault(s => s.Token == token && s.BrokerExchange == exchange);
    }

    /// <summary>Python's idea of a zero for the values that must not wipe a held one: nothing, empty text, "0", "0.0" and a number equal to zero.</summary>
    private static bool IsZero(JsonNode? node)
    {
        if (node is null) return true;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<string>(out var text)) return text is "" or "0" or "0.0";
        if (v.TryGetValue<bool>(out var flag)) return !flag;
        return v.TryGetValue<double>(out var d) && d == 0;
    }

    private static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is JsonArray a) return a.Count > 0;
        if (node is JsonObject o) return o.Count > 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var text)) return text.Length > 0;
            if (v.TryGetValue<bool>(out var flag)) return flag;
            if (v.TryGetValue<double>(out var d)) return d != 0;
        }

        return true;
    }

    private Dictionary<string, JsonNode?> Merge(string scrip, JsonObject data)
    {
        if (!cache.TryGetValue(scrip, out var held)) cache[scrip] = held = [];
        foreach (var (key, value) in data)
        {
            if (KeepNonZero.Contains(key) && IsZero(value) && held.TryGetValue(key, out var old) && old is not null && !IsZero(old)) continue;
            held[key] = value?.DeepClone();
        }

        return held;
    }

    /// <summary>Python's float(): a number, or text that reads as one. Anything else cannot be converted.</summary>
    private static double PythonFloat(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
            if (v.TryGetValue<string>(out var text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }

        throw new FormatException("Not a number.");
    }

    private static double SafeFloat(JsonNode? node)
    {
        if (node is null || (node is JsonValue v && v.TryGetValue<string>(out var text) && text is "" or "-")) return 0;
        try
        {
            return PythonFloat(node);
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static long SafeInt(JsonNode? node)
    {
        var d = SafeFloat(node);
        if (double.IsInfinity(d)) throw new OverflowException("Cannot convert infinity to an integer.");
        return double.IsNaN(d) ? 0 : (long)Math.Truncate(d);
    }

    private static JsonNode? Raw(Dictionary<string, JsonNode?> held, string name) => held.TryGetValue(name, out var value) ? value?.DeepClone() : null;

    private static JsonNode? Get(Dictionary<string, JsonNode?> held, string name) => held.GetValueOrDefault(name);

    private static FeedPublish Publish(Sub sub, JsonObject data, long nowMs, StreamMode? mode = null)
    {
        data["symbol"] = sub.Symbol;
        data["exchange"] = sub.Exchange;
        data["mode"] = (int)(mode ?? sub.Mode);
        data["timestamp"] = nowMs;
        return new FeedPublish(MarketTopics.Format(sub.Exchange, sub.Symbol, mode ?? sub.Mode), data);
    }

    private List<FeedPublish> OnTouchline(JsonObject data, long nowMs)
    {
        var sub = Find(data);
        if (sub is null) return [];
        var held = Merge(sub.Scrip, data);
        var result = new JsonObject();
        if (sub.Mode == StreamMode.Ltp)
        {
            if (held.ContainsKey("lp")) result["ltp"] = PythonFloat(held["lp"]);
            if (held.ContainsKey("ft")) result["ltt"] = Raw(held, "ft");
        }
        else if (sub.Mode == StreamMode.Quote)
        {
            result["ltp"] = SafeFloat(Get(held, "lp"));
            result["ltt"] = Raw(held, "ft");
            result["volume"] = SafeInt(Get(held, "v"));
            result["open"] = SafeFloat(Get(held, "o"));
            result["high"] = SafeFloat(Get(held, "h"));
            result["low"] = SafeFloat(Get(held, "l"));
            result["close"] = SafeFloat(Get(held, "c"));
            result["change_percent"] = SafeFloat(Get(held, "pc"));
            result["average_price"] = SafeFloat(Get(held, "ap"));
            result["oi"] = SafeInt(Get(held, "oi"));
            result["prev_oi"] = SafeInt(Get(held, "poi"));
            result["total_oi"] = SafeInt(Get(held, "toi"));
            result["bid"] = SafeFloat(Get(held, "bp1"));
            result["bid_qty"] = SafeInt(Get(held, "bq1"));
            result["ask"] = SafeFloat(Get(held, "sp1"));
            result["ask_qty"] = SafeInt(Get(held, "sq1"));
        }

        return [Publish(sub, result, nowMs)];
    }

    private List<FeedPublish> OnDepth(JsonObject data, long nowMs)
    {
        // An acknowledgement is of use only for the prices it carries.
        if (Text(data, "t") == "dk" && !new[] { "o", "h", "l", "c" }.Any(f => data.TryGetPropertyValue(f, out var value) && Truthy(value))) return [];
        var sub = Find(data);
        if (sub is null) return [];
        var held = Merge(sub.Scrip, data);
        var depth = new JsonObject
        {
            ["ltp"] = SafeFloat(Get(held, "lp")), ["ltt"] = Raw(held, "ft"), ["volume"] = SafeInt(Get(held, "v")), ["open"] = SafeFloat(Get(held, "o")), ["high"] = SafeFloat(Get(held, "h")),
            ["low"] = SafeFloat(Get(held, "l")), ["close"] = SafeFloat(Get(held, "c")), ["change_percent"] = SafeFloat(Get(held, "pc")), ["average_price"] = SafeFloat(Get(held, "ap")),
            ["ltq"] = SafeInt(Get(held, "ltq")), ["ltt_time"] = Raw(held, "ltt"), ["total_buy_qty"] = SafeInt(Get(held, "tbq")), ["total_sell_qty"] = SafeInt(Get(held, "tsq")),
            ["lower_circuit"] = SafeFloat(Get(held, "lc")), ["upper_circuit"] = SafeFloat(Get(held, "uc")), ["52w_high"] = SafeFloat(Get(held, "52h")), ["52w_low"] = SafeFloat(Get(held, "52l")),
            ["oi"] = SafeInt(Get(held, "oi")), ["prev_oi"] = SafeInt(Get(held, "poi")), ["total_oi"] = SafeInt(Get(held, "toi")),
        };
        var buy = new JsonArray();
        var sell = new JsonArray();
        for (var i = 1; i <= 5; i++)
        {
            buy.Add(new JsonObject { ["price"] = SafeFloat(Get(held, $"bp{i}")), ["quantity"] = SafeInt(Get(held, $"bq{i}")), ["orders"] = SafeInt(Get(held, $"bo{i}")) });
            sell.Add(new JsonObject { ["price"] = SafeFloat(Get(held, $"sp{i}")), ["quantity"] = SafeInt(Get(held, $"sq{i}")), ["orders"] = SafeInt(Get(held, $"so{i}")) });
        }

        depth["depth"] = new JsonObject { ["buy"] = buy, ["sell"] = sell };
        var ltp = depth["ltp"]!.GetValue<double>();
        var open = depth["open"]!.GetValue<double>();
        var high = depth["high"]!.GetValue<double>();
        var low = depth["low"]!.GetValue<double>();
        var close = depth["close"]!.GetValue<double>();
        var volume = depth["volume"]!.GetValue<long>();
        var oi = depth["oi"]!.GetValue<long>();
        var published = new List<FeedPublish> { Publish(sub, depth, nowMs, StreamMode.Depth) };
        // The quote and last price modes of the scrip take their numbers from the depth tick, named as the first subscription names the scrip.
        foreach (var other in subs.Values.Where(s => s.Scrip == sub.Scrip && IsTouchline(s.Mode)))
        {
            var result = other.Mode == StreamMode.Ltp
                ? new JsonObject { ["ltp"] = ltp }
                : new JsonObject { ["ltp"] = ltp, ["open"] = open, ["high"] = high, ["low"] = low, ["close"] = close, ["volume"] = volume, ["oi"] = oi };
            published.Add(Publish(sub, result, nowMs, other.Mode));
        }

        return published;
    }
}
