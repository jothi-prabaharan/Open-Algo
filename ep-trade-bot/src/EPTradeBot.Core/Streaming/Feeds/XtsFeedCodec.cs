using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// The XTS market-data feed (Wisdom and the brokers on the same platform): a Socket.IO connection that carries only ticks, and HTTP calls that subscribe and
/// unsubscribe. The connection is read here as Engine.IO and Socket.IO text frames (an open packet is answered with a Socket.IO connect, a ping with a pong,
/// an event is a tick); subscriptions are handed to the connection as HTTP calls once the Socket.IO connect is accepted, and again after every reconnect.
/// LTP is message code 1512, Quote 1501 and Depth 1502.
/// </summary>
public sealed class XtsFeedCodec(string broker, int batch = 0, bool routesByInstrument = false) : IFeedHttpCodec
{
    private const string Subscription = "/instruments/subscription";

    private sealed record Held(string Symbol, string Exchange, int Mode, int Segment, string Token);

    private readonly Lock gate = new();
    private readonly List<Held> held = [];
    private List<FeedHttpCall> http = [];
    private List<string> outgoing = [];
    private bool connected;

    public string Broker => broker;

    private static readonly Dictionary<string, int> Segments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NSE"] = 1, ["NFO"] = 2, ["CDS"] = 3, ["BSE"] = 11, ["BFO"] = 12, ["MCX"] = 51, ["NSE_INDEX"] = 1, ["BSE_INDEX"] = 11,
        ["NSECM"] = 1, ["NSEFO"] = 2, ["NSECD"] = 3, ["BSECM"] = 11, ["BSEFO"] = 12, ["MCXFO"] = 51,
    };

    private static int Code(int mode) => mode switch { 1 => 1512, 2 => 1501, _ => 1502 };

    private static FeedHttpCall Call(string method, Held h) =>
        new(method, Subscription, new JsonObject
        {
            ["instruments"] = new JsonArray(new JsonObject { ["exchangeSegment"] = h.Segment, ["exchangeInstrumentID"] = h.Token }),
            ["xtsMessageCode"] = Code(h.Mode),
        });

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var fresh = new List<Held>();
            foreach (var r in requests)
            {
                if (r.Mode is not (StreamMode.Ltp or StreamMode.Quote or StreamMode.Depth) || !Segments.TryGetValue(r.BrokerExchange, out var segment)) continue;
                var entry = new Held(r.Symbol, r.Exchange, (int)r.Mode, segment, r.Token);
                held.RemoveAll(h => h.Symbol == entry.Symbol && h.Exchange == entry.Exchange && h.Mode == entry.Mode);
                held.Add(entry);
                if (connected && batch == 0) http.Add(Call("POST", entry));
                else if (connected) fresh.Add(entry);
            }

            http.AddRange(Batches(fresh));
            return [];
        }
    }

    /// <summary>Instruments of one mode go in one request, up to the batch size; the first mode waiting goes first.</summary>
    private IEnumerable<FeedHttpCall> Batches(IReadOnlyList<Held> items)
    {
        var rest = items.ToList();
        while (rest.Count > 0)
        {
            var mode = rest[0].Mode;
            var group = rest.Where(h => h.Mode == mode).Take(batch).ToList();
            foreach (var g in group) rest.Remove(g);
            yield return new FeedHttpCall("POST", Subscription, new JsonObject
            {
                ["instruments"] = new JsonArray(group.Select(h => (JsonNode?)new JsonObject { ["exchangeSegment"] = h.Segment, ["exchangeInstrumentID"] = h.Token }).ToArray()),
                ["xtsMessageCode"] = Code(mode),
            });
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            foreach (var m in mode is { } one ? [(int)one] : new[] { 1, 2, 3 })
            {
                var entry = held.FirstOrDefault(h => h.Symbol == target.Symbol && h.Exchange == target.Exchange && h.Mode == m);
                if (entry is null) continue;
                held.Remove(entry);
                if (connected) http.Add(Call("PUT", entry));
            }

            return [];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            connected = false;
            return [];
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

    public void Reset()
    {
        lock (gate)
        {
            held.Clear();
            http = [];
            outgoing = [];
            connected = false;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => [];

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs)
    {
        lock (gate)
        {
            if (frame.Length == 0) return [];
            if (frame[0] == '{') return Tick(Parse(frame), nowMs);
            if (frame == "2")
            {
                outgoing.Add("3");
                return [];
            }

            if (frame[0] == '0')
            {
                // The Engine.IO open packet: the Socket.IO connect goes next.
                outgoing.Add("40");
                return [];
            }

            if (frame.StartsWith("40", StringComparison.Ordinal))
            {
                connected = true;
                if (batch == 0) foreach (var h in held) http.Add(Call("POST", h));
                else http.AddRange(Batches(held));
                return [];
            }

            if (frame.StartsWith("41", StringComparison.Ordinal))
            {
                connected = false;
                return [];
            }

            if (!frame.StartsWith("42", StringComparison.Ordinal)) return [];
            JsonNode? packet;
            try
            {
                packet = JsonNode.Parse(frame[2..]);
            }
            catch (JsonException)
            {
                return [];
            }

            if (packet is not JsonArray { Count: >= 2 } array || array[0] is not JsonValue name || !name.TryGetValue<string>(out var eventName)) return [];
            if (!eventName.EndsWith("-json-full", StringComparison.Ordinal) && !eventName.EndsWith("-json-partial", StringComparison.Ordinal)) return [];
            var payload = array[1];
            if (payload is JsonValue text && text.TryGetValue<string>(out var json)) payload = Parse(json);
            return Tick(payload as JsonObject, nowMs);
        }
    }

    private static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    private List<FeedPublish> Tick(JsonObject? data, long nowMs)
    {
        if (data is null) return [];
        var code = Text(data["MessageCode"]) switch { "1512" => 1, "1501" => 2, "1502" => 3, _ => 0 };
        if ((code == 0 && !routesByInstrument) || !int.TryParse(Text(data["ExchangeSegment"]), out var segment)) return [];
        var token = Text(data["ExchangeInstrumentID"]);
        var result = new List<FeedPublish>();
        foreach (var h in held.Where(h => (routesByInstrument || h.Mode == code) && h.Segment == segment && h.Token == token))
        {
            var mode = h.Mode;
            var tick = Normalize(data, mode);
            tick["symbol"] = h.Symbol;
            tick["exchange"] = h.Exchange;
            tick["mode"] = mode;
            tick["timestamp"] = nowMs;
            result.Add(new FeedPublish($"{h.Exchange}_{h.Symbol}_{mode switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" }}", tick));
        }

        return result;
    }

    private static JsonNode? Get(JsonObject o, string key, JsonNode? fallback = null) => o.ContainsKey(key) ? o[key]?.DeepClone() : fallback ?? JsonValue.Create(0);

    private static JsonObject Normalize(JsonObject message, int mode)
    {
        var source = Text(message["MessageCode"]) == "1502" && message["Touchline"] is JsonObject touchline ? touchline : message;
        var average = source == message ? (message.ContainsKey("AveragePrice") ? Get(message, "AveragePrice") : Get(message, "AverageTradedPrice")) : Get(source, "AverageTradedPrice");
        var quantity = source.ContainsKey("LastTradedQunatity") ? Get(source, "LastTradedQunatity") : Get(source, "LastTradedQuantity");
        var ltp = Get(source, "LastTradedPrice");
        var ltt = Get(source, "LastTradedTime");
        switch (mode)
        {
            case 1:
                return new JsonObject { ["ltp"] = ltp, ["ltt"] = ltt, ["ltq"] = quantity };
            case 2:
                return new JsonObject
                {
                    ["ltp"] = ltp, ["ltt"] = ltt, ["volume"] = Get(source, "TotalTradedQuantity"), ["open"] = Get(source, "Open"), ["high"] = Get(source, "High"), ["low"] = Get(source, "Low"),
                    ["close"] = Get(source, "Close"), ["last_quantity"] = quantity, ["average_price"] = average, ["total_buy_quantity"] = Get(source, "TotalBuyQuantity"),
                    ["total_sell_quantity"] = Get(source, "TotalSellQuantity"),
                };
            default:
                var result = new JsonObject
                {
                    ["ltp"] = ltp, ["ltt"] = ltt, ["volume"] = Get(source, "TotalTradedQuantity"), ["open"] = Get(source, "Open"), ["high"] = Get(source, "High"), ["low"] = Get(source, "Low"),
                    ["close"] = Get(source, "Close"), ["oi"] = Get(message, "OpenInterest"), ["upper_circuit"] = Get(message, "UpperCircuitLimit"), ["lower_circuit"] = Get(message, "LowerCircuitLimit"),
                };
                if (message.ContainsKey("Bids") && message.ContainsKey("Asks"))
                    result["depth"] = new JsonObject { ["buy"] = Levels(message["Bids"] as JsonArray), ["sell"] = Levels(message["Asks"] as JsonArray) };
                return result;
        }
    }

    private static JsonArray Levels(JsonArray? levels)
    {
        var result = new JsonArray();
        foreach (var level in levels ?? [])
        {
            if (level is JsonObject o)
                result.Add(new JsonObject { ["price"] = Get(o, "Price"), ["quantity"] = Get(o, "Size"), ["orders"] = Get(o, "TotalOrders") });
        }

        while (result.Count < 5) result.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 });
        while (result.Count > 20) result.RemoveAt(result.Count - 1);
        return result;
    }
}
