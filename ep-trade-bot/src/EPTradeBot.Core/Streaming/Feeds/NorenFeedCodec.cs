using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// The Noren market-data socket (Shoonya, Zebu, Flattrade): JSON over a websocket that is signed in by its first message. Touchline (LTP and Quote) and depth
/// are separate subscriptions, each a "#"-joined list of "EXCHANGE|token" scrips in batches of at most 100. A subscription is counted per instrument and
/// mode, and the broker is told only when the first one of a kind arrives and when the last one leaves. A frame names its exchange and token; the cache of
/// each scrip keeps a non-zero open, high, low, close and average price over a zero that follows it.
/// </summary>
public sealed class NorenFeedCodec(string broker, string source = "API") : IFeedCodec
{
    private const int BatchSize = 100;

    private sealed record Owner(string Symbol, string Exchange, int Mode);

    private readonly Lock gate = new();
    private readonly List<(string Scrip, Owner Owner)> owners = [];
    private readonly Dictionary<string, (string Symbol, string Exchange)> symbolOf = [];
    private readonly Dictionary<string, (int Touchline, int Depth)> refs = [];
    private readonly Dictionary<string, JsonObject> cache = [];
    private List<string> outgoing = [];
    private string userId = "";
    private string token = "";
    private bool authenticated;

    public string Broker => broker;

    /// <summary>True when the broker refused the last sign-in (an expired token); the host reads it to stop retrying a token that will be refused again.</summary>
    public bool SignInRefused { get; private set; }

    private static readonly Dictionary<string, string> Exchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NSE"] = "NSE", ["BSE"] = "BSE", ["NFO"] = "NFO", ["BFO"] = "BFO", ["MCX"] = "MCX", ["CDS"] = "CDS", ["NSE_INDEX"] = "NSE", ["BSE_INDEX"] = "BSE",
    };

    public void Begin(BrokerSession session)
    {
        lock (gate)
        {
            var key = session.Keys.TryGetValue("ApiKey", out var k) ? k : "";
            userId = key.Contains(":::", StringComparison.Ordinal) ? key.Split(":::")[0] : key;
            token = session.AuthToken;
            authenticated = false;
            SignInRefused = false;
        }
    }

    private string SignIn() => new JsonObject { ["t"] = "a", ["uid"] = userId, ["actid"] = userId, ["source"] = source, ["accesstoken"] = token }.ToJsonString();

    private static IEnumerable<string> Batches(string type, IReadOnlyList<string> scrips)
    {
        for (var i = 0; i < scrips.Count; i += BatchSize)
            yield return new JsonObject { ["t"] = type, ["k"] = string.Join('#', scrips.Skip(i).Take(BatchSize)) }.ToJsonString();
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var touchline = new List<string>();
            var depth = new List<string>();
            foreach (var r in requests)
            {
                if (r.Mode is not (StreamMode.Ltp or StreamMode.Quote or StreamMode.Depth) || !Exchanges.TryGetValue(r.BrokerExchange, out var exchange)) continue;
                var scrip = $"{exchange}|{r.Token}";
                var mode = (int)r.Mode;
                var first = !owners.Any(o => o.Scrip == scrip && o.Owner.Symbol == r.Symbol && o.Owner.Exchange == r.Exchange && o.Owner.Mode == mode);
                owners.Add((scrip, new Owner(r.Symbol, r.Exchange, mode)));
                symbolOf[scrip] = (r.Symbol, r.Exchange);
                if (!first) continue;
                var have = refs.GetValueOrDefault(scrip);
                if (mode == 3)
                {
                    refs[scrip] = (have.Touchline, have.Depth + 1);
                    if (have.Depth == 0 && !depth.Contains(scrip)) depth.Add(scrip);
                }
                else
                {
                    refs[scrip] = (have.Touchline + 1, have.Depth);
                    if (have.Touchline == 0 && !touchline.Contains(scrip)) touchline.Add(scrip);
                }
            }

            // Nothing is sent before the sign-in is accepted; the accepted sign-in sends every subscription held.
            return authenticated ? [.. Batches("t", touchline), .. Batches("d", depth)] : [];
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var sent = new List<string>();
            foreach (var m in mode is { } one ? [(int)one] : new[] { 1, 2, 3 })
            {
                var mine = owners.Where(o => o.Owner.Symbol == target.Symbol && o.Owner.Exchange == target.Exchange && o.Owner.Mode == m).ToList();
                if (mine.Count == 0) continue;
                var scrip = mine[0].Scrip;
                if (mine.Count == 1)
                {
                    var have = refs.GetValueOrDefault(scrip);
                    var count = m == 3 ? have.Depth : have.Touchline;
                    if (count == 1 && authenticated) sent.Add(new JsonObject { ["t"] = m == 3 ? "ud" : "u", ["k"] = scrip }.ToJsonString());
                    if (count > 0)
                    {
                        have = m == 3 ? (have.Touchline, have.Depth - 1) : (have.Touchline - 1, have.Depth);
                        if (have.Touchline <= 0 && have.Depth <= 0) refs.Remove(scrip);
                        else refs[scrip] = have;
                    }
                }

                owners.Remove(mine[0]);
                if (owners.All(o => o.Scrip != scrip))
                {
                    symbolOf.Remove(scrip);
                    cache.Remove(scrip);
                }
            }

            return sent;
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            authenticated = false;
            return [SignIn()];
        }
    }

    public IReadOnlyList<string> TakeOutgoing()
    {
        lock (gate)
        {
            var taken = outgoing;
            outgoing = [];
            return taken;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            owners.Clear();
            symbolOf.Clear();
            refs.Clear();
            cache.Clear();
            outgoing = [];
            authenticated = false;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs)
    {
        JsonObject? data;
        try
        {
            data = JsonNode.Parse(frame) as JsonObject;
        }
        catch (JsonException)
        {
            return [];
        }

        if (data is null) return [];
        var type = Text(data["t"]);
        lock (gate)
        {
            switch (type)
            {
                case "ak":
                    if (Text(data["s"]) == "OK") Accept();
                    else SignInRefused = true;
                    return [];
                case "hk" or "h":
                    return [];
                case "tf" or "tk" or "df" or "dk":
                    return Market(data, type, nowMs);
                default:
                    return [];
            }
        }
    }

    /// <summary>The sign-in is accepted: what was cached before is stale, and every subscription held is sent again.</summary>
    private void Accept()
    {
        authenticated = true;
        cache.Clear();
        refs.Clear();
        var touchline = new List<string>();
        var depth = new List<string>();
        foreach (var (scrip, owner) in owners)
        {
            var have = refs.GetValueOrDefault(scrip);
            if (owner.Mode == 3)
            {
                refs[scrip] = (have.Touchline, 1);
                if (!depth.Contains(scrip)) depth.Add(scrip);
            }
            else
            {
                refs[scrip] = (1, have.Depth);
                if (!touchline.Contains(scrip)) touchline.Add(scrip);
            }
        }

        outgoing = [.. Batches("t", touchline), .. Batches("d", depth)];
    }

    private List<FeedPublish> Market(JsonObject data, string type, long nowMs)
    {
        var tk = Text(data["tk"]);
        if (string.IsNullOrEmpty(tk)) return [];
        var scrip = Resolve(Text(data["e"]), tk);
        if (scrip is null || !symbolOf.TryGetValue(scrip, out var named)) return [];
        var wantsDepth = type is "df" or "dk";
        var result = new List<FeedPublish>();
        foreach (var (s, owner) in owners.Where(o => o.Scrip == scrip).ToList())
        {
            if ((owner.Mode == 3) != wantsDepth) continue;
            var merged = Merge(scrip, data);
            var tick = owner.Mode switch { 1 => Ltp(merged), 2 => Quote(merged), _ => Depth(merged) };
            tick["symbol"] = named.Symbol;
            tick["exchange"] = named.Exchange;
            tick["timestamp"] = nowMs;
            result.Add(new FeedPublish($"{named.Exchange}_{named.Symbol}_{owner.Mode switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" }}", tick));
        }

        return result;
    }

    /// <summary>A frame names its exchange. Without one the token alone has to name a single subscribed scrip, because two exchanges share tokens.</summary>
    private string? Resolve(string? exchange, string tk)
    {
        if (!string.IsNullOrEmpty(exchange))
        {
            var scrip = $"{exchange}|{tk}";
            return symbolOf.ContainsKey(scrip) ? scrip : null;
        }

        var candidates = symbolOf.Keys.Where(s => s[(s.IndexOf('|') + 1)..] == tk).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private JsonObject Merge(string scrip, JsonObject data)
    {
        if (!cache.TryGetValue(scrip, out var held)) cache[scrip] = held = [];
        foreach (var (key, value) in data)
        {
            if (key is "o" or "h" or "l" or "c" or "ap" && IsZero(value) && held[key] is { } old && !IsZero(old)) continue;
            held[key] = value?.DeepClone();
        }

        return held;
    }

    private static bool IsZero(JsonNode? node) => node switch
    {
        null => true,
        JsonValue v when v.TryGetValue<string>(out var s) => s is "" or "0" or "0.0",
        JsonValue v when v.TryGetValue<double>(out var d) => d == 0,
        _ => false,
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : null;

    private static double Float(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && s is not ("" or "-") && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0;
    }

    private static long Int(JsonNode? node) => (long)Math.Truncate(Float(node));

    private static JsonObject Ltp(JsonObject d) => new() { ["mode"] = 1, ["ltp"] = Float(d["lp"]), ["last_trade_time"] = d["ltt"]?.DeepClone(), ["shoonya_timestamp"] = Int(d["ft"]) };

    private static JsonObject Quote(JsonObject d) => new()
    {
        ["mode"] = 2, ["ltp"] = Float(d["lp"]), ["volume"] = Int(d["v"]), ["open"] = Float(d["o"]), ["high"] = Float(d["h"]), ["low"] = Float(d["l"]), ["close"] = Float(d["c"]),
        ["average_price"] = Float(d["ap"]), ["percent_change"] = Float(d["pc"]), ["last_quantity"] = Int(d["ltq"]), ["last_trade_time"] = d["ltt"]?.DeepClone(), ["shoonya_timestamp"] = Int(d["ft"]),
    };

    private static JsonObject Depth(JsonObject d)
    {
        JsonArray Side(string price, string quantity, string orders) =>
            new(Enumerable.Range(1, 5).Select(i => (JsonNode?)new JsonObject { ["price"] = Float(d[$"{price}{i}"]), ["quantity"] = Int(d[$"{quantity}{i}"]), ["orders"] = Int(d[$"{orders}{i}"]) }).ToArray());

        return new JsonObject
        {
            ["mode"] = 3, ["ltp"] = Float(d["lp"]), ["volume"] = Int(d["v"]), ["open"] = Float(d["o"]), ["high"] = Float(d["h"]), ["low"] = Float(d["l"]), ["close"] = Float(d["c"]),
            ["average_price"] = Float(d["ap"]), ["percent_change"] = Float(d["pc"]), ["last_quantity"] = Int(d["ltq"]), ["last_trade_time"] = d["ltt"]?.DeepClone(),
            ["total_buy_quantity"] = Int(d["tbq"]), ["total_sell_quantity"] = Int(d["tsq"]), ["shoonya_timestamp"] = Int(d["ft"]),
            ["depth"] = new JsonObject { ["buy"] = Side("bp", "bq", "bo"), ["sell"] = Side("sp", "sq", "so") }, ["depth_level"] = 5,
            ["upper_circuit"] = Float(d["uc"]), ["lower_circuit"] = Float(d["lc"]), ["52_week_high"] = Float(d["52h"]), ["52_week_low"] = Float(d["52l"]), ["open_interest"] = Int(d["toi"]),
        };
    }
}
