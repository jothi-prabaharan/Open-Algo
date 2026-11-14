using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Samco's stream (broker/samco/streaming): JSON over a WebSocket. Every subscribe frame restates the whole set of tokens, once per stream (quote2 carries the depth ladder, quote carries prices), so what is sent after any
/// change is the full set; a depth subscriber is on both streams. A token has one mode on the connection, the last asked for. Quote and depth frames each carry only half of what a depth subscriber needs, so the fields of a
/// token are merged across frames. An index is streamed under a listing id Samco hands out over HTTP, asked for once; the frames for a call that needs one are held until the answers are in, so a burst is one frame pair.
/// </summary>
public sealed class SamcoFeedCodec : IFeedHttpCodec
{
    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, StreamMode> state = [];
    private readonly OrderedDictionary<(string Symbol, string Exchange, StreamMode Mode), (string Token, string BrokerExchange)> subs = [];
    private readonly Dictionary<string, JsonObject> merged = [];
    private readonly Dictionary<(string Exchange, string Symbol), string> listings = [];
    private readonly List<FeedRequest> queue = [];
    private readonly HashSet<(string Exchange, string Symbol)> asked = [];
    private List<FeedHttpCall> http = [];
    private List<string> outgoing = [];
    private bool dirty;

    public string Broker => "samco";

    private static bool IsIndex(string exchange) => exchange is "NSE_INDEX" or "BSE_INDEX";

    private static string Request(string streamingType, string requestType, IEnumerable<string> keys) =>
        new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["streaming_type"] = streamingType,
                ["data"] = new JsonObject { ["symbols"] = new JsonArray(keys.Select(k => (JsonNode?)new JsonObject { ["symbol"] = k }).ToArray()) },
                ["request_type"] = requestType,
                ["response_format"] = "json",
            },
        }.ToJsonString() + "\n";

    /// <summary>The whole current set: the depth tokens on quote2, then every token on quote.</summary>
    private List<string> FullSet()
    {
        var frames = new List<string>();
        var depthKeys = state.Where(s => s.Value == StreamMode.Depth).Select(s => s.Key).ToList();
        if (depthKeys.Count > 0) frames.Add(Request("quote2", "subscribe", depthKeys));
        if (state.Count > 0) frames.Add(Request("quote", "subscribe", state.Keys.ToList()));
        return frames;
    }

    private static string SegmentOf(string brokerExchange) => brokerExchange is "NSE" or "NFO" or "BSE" or "BFO" or "MCX" or "MFO" or "CDS" ? brokerExchange : "NSE";

    /// <summary>The key a token goes under on the stream: an index's listing id as it is, a token already carrying its segment as it is, a bare script code with its segment added.</summary>
    private static string KeyOf(string token, string brokerExchange) => token.StartsWith('-') || token.Contains('_', StringComparison.Ordinal) ? token : $"{token}_{SegmentOf(brokerExchange)}";

    private void Add(string symbol, string exchange, StreamMode mode, string token, string brokerExchange)
    {
        subs[(symbol, exchange, mode)] = (token, brokerExchange);
        state[KeyOf(token, brokerExchange)] = mode;
        dirty = true;
    }

    private void Take(FeedRequest r)
    {
        if (IsIndex(r.Exchange))
        {
            if (listings.TryGetValue((r.Exchange, r.Symbol), out var listing)) Add(r.Symbol, r.Exchange, r.Mode, listing, r.Exchange == "NSE_INDEX" ? "NSE" : "BSE");
            return;
        }

        if (!string.IsNullOrEmpty(r.Token)) Add(r.Symbol, r.Exchange, r.Mode, r.Token, r.BrokerExchange);
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            // A call that needs a listing id it does not have yet is held whole, with whatever is already held, so the instruments go on the stream in the order they were asked for.
            foreach (var r in requests.Where(r => IsIndex(r.Exchange) && !listings.ContainsKey((r.Exchange, r.Symbol))))
            {
                if (asked.Add((r.Exchange, r.Symbol)))
                    http.Add(new FeedHttpCall("GET", $"/quote/indexQuote?indexName={Uri.EscapeDataString(r.BrokerSymbol ?? r.Symbol).Replace("%2F", "/", StringComparison.Ordinal)}", new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange }));
            }

            if (asked.Count > 0)
            {
                queue.AddRange(requests);
                return [];
            }

            foreach (var r in requests) Take(r);
            if (!dirty) return [];
            dirty = false;
            return FullSet();
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            string token, brokerExchange;
            if (IsIndex(target.Exchange))
            {
                if (!listings.TryGetValue((target.Exchange, target.Symbol), out var listing)) return [];
                (token, brokerExchange) = (listing, target.Exchange == "NSE_INDEX" ? "NSE" : "BSE");
            }
            else
            {
                (token, brokerExchange) = (target.Token, target.BrokerExchange);
            }

            var effective = mode ?? (subs.ContainsKey((target.Symbol, target.Exchange, StreamMode.Depth)) ? StreamMode.Depth : StreamMode.Quote);
            var key = KeyOf(token, brokerExchange);
            var frames = new List<string>();
            if (effective == StreamMode.Depth) frames.Add(Request("quote2", "unsubscribe", [key]));
            frames.Add(Request("quote", "unsubscribe", [key]));

            if (mode is null)
            {
                foreach (var m in Enum.GetValues<StreamMode>()) subs.Remove((target.Symbol, target.Exchange, m));
            }
            else
            {
                subs.Remove((target.Symbol, target.Exchange, effective));
            }

            state.Remove(key);
            merged.Remove(key);
            dirty = false;
            frames.AddRange(FullSet());
            return frames;
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            // Each mode's subscriptions are asked again in the order the modes first appeared, so a token held in two modes ends up in the one asked last in that order.
            foreach (var mode in subs.Keys.Select(k => k.Mode).Distinct().ToList())
            {
                foreach (var (key, value) in subs.Where(s => s.Key.Mode == mode).ToList()) state[KeyOf(value.Token, value.BrokerExchange)] = mode;
            }

            dirty = false;
            return FullSet();
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            state.Clear();
            subs.Clear();
            merged.Clear();
            listings.Clear();
            queue.Clear();
            asked.Clear();
            http = [];
            outgoing = [];
            dirty = false;
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

    public IReadOnlyList<FeedHttpCall> TakeHttp()
    {
        lock (gate)
        {
            var taken = http;
            http = [];
            return taken;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    // ---- reading -----------------------------------------------------------------------------------------------

    private static double Float(JsonNode? node)
    {
        if (node is null) return 0.0;
        if (node is JsonValue v && v.TryGetValue<string>(out var text))
        {
            if (text.Length == 0) return 0.0;
            return double.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }

        return node is JsonValue n && n.TryGetValue<double>(out var number) ? number : 0.0;
    }

    private static long Whole(JsonNode? node) => double.IsFinite(Float(node)) ? (long)Math.Truncate(Float(node)) : 0;

    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is null ? "" : node.ToJsonString().Trim('"');

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
            if (data["samco_index"] is not null) return IndexAnswer(data);

            string type;
            JsonObject payload;
            if (data["response"] is JsonObject response)
            {
                type = response["streaming_type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "";
                payload = response["data"] as JsonObject ?? [];
            }
            else
            {
                type = data["streaming_type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "";
                payload = data;
            }

            if (type is not ("quote" or "quote2" or "marketDepth")) return [];
            return Route(payload, type is "quote2" or "marketDepth", nowMs);
        }
    }

    private IReadOnlyList<FeedPublish> IndexAnswer(JsonObject answer)
    {
        var symbol = Text(answer["symbol"]);
        var exchange = Text(answer["exchange"]);
        if (answer["listing_id"] is { } id && Truthy(id)) listings[(exchange, symbol)] = Text(id);
        asked.Remove((exchange, symbol));
        if (asked.Count > 0) return [];

        var held = queue.ToList();
        queue.Clear();
        foreach (var r in held) Take(r);
        if (dirty)
        {
            dirty = false;
            outgoing.AddRange(FullSet());
        }

        return [];
    }

    private IReadOnlyList<FeedPublish> Route(JsonObject payload, bool depthStream, long nowMs)
    {
        var key = Truthy(payload["symbol"]) ? Text(payload["symbol"]) : Truthy(payload["sym"]) ? Text(payload["sym"]) : "";
        if (key.Length == 0) return [];

        if (!merged.TryGetValue(key, out var held)) merged[key] = held = [];
        foreach (var (name, value) in payload)
        {
            if (value is null || (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0)) continue;
            held[name] = value.DeepClone();
        }

        var bids = held["bidValues"] as JsonArray ?? [];
        var asks = held["askValues"] as JsonArray ?? [];
        var bestBidPrice = bids.Count > 0 ? Float((bids[0] as JsonObject)?["price"]) : Float(held["bPr"]);
        var bestBidQuantity = bids.Count > 0 ? Whole((bids[0] as JsonObject)?["qty"]) : Whole(held["bSz"]);
        var bestAskPrice = asks.Count > 0 ? Float((asks[0] as JsonObject)?["price"]) : Float(held["aPr"]);
        var bestAskQuantity = asks.Count > 0 ? Whole((asks[0] as JsonObject)?["qty"]) : Whole(held["aSz"]);

        JsonObject? depth = null;
        if (bids.Count > 0 || asks.Count > 0)
        {
            depth = new JsonObject
            {
                ["buy"] = new JsonArray(bids.OfType<JsonObject>().Select(l => (JsonNode?)new JsonObject { ["price"] = Float(l["price"]), ["quantity"] = Whole(l["qty"]), ["orders"] = Whole(l["no"]) }).ToArray()),
                ["sell"] = new JsonArray(asks.OfType<JsonObject>().Select(l => (JsonNode?)new JsonObject { ["price"] = Float(l["price"]), ["quantity"] = Whole(l["qty"]), ["orders"] = Whole(l["no"]) }).ToArray()),
            };
        }

        var targets = depthStream ? new[] { StreamMode.Depth } : [StreamMode.Ltp, StreamMode.Quote];
        var published = new List<FeedPublish>();
        foreach (var ((symbol, exchange, mode), sub) in subs)
        {
            if (!targets.Contains(mode)) continue;
            if (sub.Token != key && symbol != key && key != $"{sub.Token}_{sub.BrokerExchange}" && !(key.Contains('_', StringComparison.Ordinal) && key.Split('_')[0] == sub.Token)) continue;

            var data = new JsonObject { ["ltp"] = Float(held["ltp"]), ["ltt"] = nowMs };
            if (mode != StreamMode.Ltp)
            {
                data["volume"] = Whole(held["vol"]);
                data["open"] = Float(held["o"]);
                data["high"] = Float(held["h"]);
                data["low"] = Float(held["l"]);
                data["close"] = Float(held["c"]);
                data[mode == StreamMode.Quote ? "last_trade_quantity" : "last_quantity"] = Whole(held["ltq"]);
                data["change"] = Float(held["ch"]);
                data["change_percentage"] = Float(held["chPer"]);
                if (mode == StreamMode.Quote)
                {
                    data["best_bid_price"] = bestBidPrice;
                    data["best_bid_quantity"] = bestBidQuantity;
                    data["best_ask_price"] = bestAskPrice;
                    data["best_ask_quantity"] = bestAskQuantity;
                }
                else if (depth is not null)
                {
                    data["depth"] = depth.DeepClone();
                }
            }

            data["symbol"] = symbol;
            data["exchange"] = exchange;
            data["mode"] = (int)mode;
            data["timestamp"] = nowMs;
            published.Add(new FeedPublish(MarketTopics.Format(exchange, symbol, mode), data));
        }

        return published;
    }
}
