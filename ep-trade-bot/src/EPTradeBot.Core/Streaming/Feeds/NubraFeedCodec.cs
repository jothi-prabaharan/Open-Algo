using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>The text commands Nubra's socket takes (broker/nubra/api/nubrawebsocket.py): batch_subscribe and batch_unsubscribe, the session token, a channel and a JSON list of reference ids and names.</summary>
public static class NubraCommands
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>OpenAlgo's index name on a frame (upper case) to the symbol it is subscribed as.</summary>
    public static readonly IReadOnlyDictionary<string, string> IndexNames = new Dictionary<string, string>
    {
        ["NIFTY 50"] = "NIFTY", ["NIFTY BANK"] = "BANKNIFTY", ["NIFTY FINANCIAL SERVICES"] = "FINNIFTY", ["BSE SENSEX"] = "SENSEX", ["BSE SENSEX 50"] = "SENSEX50",
    };

    public static string Payload(IEnumerable<long>? refs, IEnumerable<string>? names) =>
        new JsonObject { ["instruments"] = new JsonArray((refs ?? []).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()), ["indexes"] = new JsonArray((names ?? []).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) }.ToJsonString(Compact);

    public static string Command(string verb, string token, string channel, string payload, string? tail = null) => $"{verb} {token} {channel} {payload}{(tail is null ? "" : " " + tail)}";
}

/// <summary>
/// Nubra's market data stream (broker/nubra/streaming/nubra_adapter.py). LTP and Quote subscribe the instrument (or an index by its exchange name) on the index channel and its one-day candle bucket; Depth subscribes the order book by reference id. Requests made together
/// go out together, grouped by exchange, the books last, as OpenAlgo sends what it gathers in half a second. A frame is read by the kind of message it holds: an index message gives the Quote and LTP ticks of what it names (a Quote tick takes its open and close from the
/// bucket last seen for that name), a book gives the LTP, Quote and Depth ticks of the reference id it names, and a bucket only remembers its open and close. A reconnect repeats the gathered requests that were sent and not taken back by one that names exactly the same
/// symbols, so a request that was one of several is repeated whole, as OpenAlgo repeats it.
/// </summary>
public sealed class NubraFeedCodec : IFeedCodec
{
    private sealed record Info(string Symbol, string Exchange, string? SubName, string? NubraExchange, long? RefId);

    private sealed record Batch(string Kind, IReadOnlyList<string> Names, string? Exchange, string? Interval);

    private sealed record Pending(string Channel, string SubName, string NubraExchange, long RefId);

    private static readonly IReadOnlyDictionary<string, string> Subscription = new Dictionary<string, string>
    {
        ["NIFTY"] = "Nifty 50", ["BANKNIFTY"] = "Nifty Bank", ["FINNIFTY"] = "Nifty Financial Services", ["SENSEX"] = "Bse Sensex", ["SENSEX50"] = "Bse Sensex 50",
    };

    private static readonly IReadOnlyDictionary<string, string> Exchanges = new Dictionary<string, string>
    {
        ["NSE"] = "NSE", ["BSE"] = "BSE", ["NFO"] = "NSE", ["BFO"] = "BSE", ["MCX"] = "MCX", ["NSE_INDEX"] = "NSE", ["BSE_INDEX"] = "BSE",
    };

    private readonly Lock gate = new();
    private readonly Dictionary<string, Info> subscribed = [];
    private readonly Dictionary<string, (string Symbol, string Exchange)> indexNames = [];
    private readonly Dictionary<long, (string Symbol, string Exchange)> refIds = [];
    private readonly Dictionary<string, HashSet<int>> modes = [];
    private readonly HashSet<string> indexChannel = [];
    private readonly HashSet<string> bucketChannel = [];
    private readonly HashSet<long> bookChannel = [];
    private readonly Dictionary<string, (double Open, double Close)> buckets = [];
    private readonly OrderedDictionary<string, Batch> sent = [];
    private string token = "";

    public string Broker => "nubra";

    public void Begin(BrokerSession session)
    {
        lock (gate) token = session.AuthToken;
    }

    private static bool IsIndex(string exchange) => exchange is "NSE_INDEX" or "BSE_INDEX";

    private static string KeyOf(string symbol, string exchange) => $"{exchange}:{symbol}";

    private static string BatchKey(string kind, IReadOnlyList<string> names, string? exchange, string? interval) => $"{kind}\u0001{exchange}\u0001{interval}\u0001{string.Join('\u0002', names)}";

    private string Send(string kind, IReadOnlyList<string> names, string? exchange, string? interval)
    {
        sent[BatchKey(kind, names, exchange, interval)] = new Batch(kind, names, exchange, interval);
        return Message("batch_subscribe", kind, names, exchange, interval);
    }

    private string Message(string verb, string kind, IReadOnlyList<string> names, string? exchange, string? interval) => kind switch
    {
        "index" => NubraCommands.Command(verb, token, "index", NubraCommands.Payload(null, names), exchange ?? "NSE"),
        "ohlcv" => NubraCommands.Command(verb, token, "index_bucket", NubraCommands.Payload(null, names), $"{interval} {exchange ?? "NSE"}"),
        _ => NubraCommands.Command(verb, token, kind, NubraCommands.Payload(names.Where(n => n.Length > 0 && n.All(char.IsAsciiDigit)).Select(n => long.Parse(n, CultureInfo.InvariantCulture)), null)),
    };

    private string Unsend(string kind, IReadOnlyList<string> names, string? exchange, string? interval)
    {
        sent.Remove(BatchKey(kind, names, exchange, interval));
        return Message("batch_unsubscribe", kind, names, exchange, interval);
    }

    // ---- subscribing -------------------------------------------------------------------------------------------

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var queue = new List<Pending>();
            foreach (var r in requests) SubscribeOne(r, queue);
            var messages = new List<string>();
            var byExchange = new OrderedDictionary<string, List<string>>();
            var books = new List<long>();
            foreach (var item in queue)
            {
                if (item.Channel == "index")
                {
                    if (!byExchange.TryGetValue(item.NubraExchange, out var names)) byExchange[item.NubraExchange] = names = [];
                    names.Add(item.SubName);
                }
                else
                {
                    books.Add(item.RefId);
                }
            }

            foreach (var (exchange, names) in byExchange)
            {
                messages.Add(Send("index", names, exchange, null));
                messages.Add(Send("ohlcv", names, exchange, "1d"));
            }

            if (books.Count > 0) messages.Add(Send("orderbook", books.Select(b => b.ToString(CultureInfo.InvariantCulture)).ToList(), null, null));
            return messages;
        }
    }

    private void SubscribeOne(FeedRequest r, List<Pending> queue)
    {
        var mode = (int)r.Mode;
        if (mode is < 1 or > 3) return;
        var isIndex = IsIndex(r.Exchange);
        if (mode == 3 && isIndex) return;
        var key = KeyOf(r.Symbol, r.Exchange);
        if (mode != 3) SubscribeIndexChannel(r, mode, key, isIndex, queue);
        else SubscribeBook(r, mode, key, queue);
    }

    private void SubscribeIndexChannel(FeedRequest r, int mode, string key, bool isIndex, List<Pending> queue)
    {
        var nubraExchange = Exchanges.GetValueOrDefault(r.Exchange, "NSE");
        string subName;
        if (isIndex)
        {
            subName = Subscription.GetValueOrDefault(r.Symbol, "");
            if (subName.Length == 0) subName = r.BrokerSymbol ?? "";
            if (subName.Length == 0) return;
        }
        else
        {
            subName = r.Symbol;
        }

        if (!indexChannel.Contains(key))
        {
            queue.Add(new Pending("index", subName, nubraExchange, 0));
            indexChannel.Add(key);
            bucketChannel.Add(key);
        }

        indexNames[subName.ToUpperInvariant()] = (r.Symbol, r.Exchange);
        if (isIndex)
            foreach (var (wsName, symbol) in NubraCommands.IndexNames)
                if (symbol == r.Symbol) indexNames[wsName] = (r.Symbol, r.Exchange);

        if (!modes.TryGetValue(key, out var set)) modes[key] = set = [];
        set.Add(mode);
        subscribed[key] = new Info(r.Symbol, r.Exchange, subName, nubraExchange, null);
    }

    private void SubscribeBook(FeedRequest r, int mode, string key, List<Pending> queue)
    {
        if (string.IsNullOrEmpty(r.Token)) return;
        var text = r.Token.Contains("::::", StringComparison.Ordinal) ? r.Token[..r.Token.IndexOf("::::", StringComparison.Ordinal)] : r.Token.Contains(':') ? r.Token[..r.Token.IndexOf(':')] : r.Token;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var refId)) return;
        if (!bookChannel.Contains(refId))
        {
            queue.Add(new Pending("orderbook", "", "", refId));
            bookChannel.Add(refId);
        }

        refIds[refId] = (r.Symbol, r.Exchange);
        if (!modes.TryGetValue(key, out var set)) modes[key] = set = [];
        set.Add(mode);
        subscribed[key] = new Info(r.Symbol, r.Exchange, null, null, refId);
    }

    // ---- unsubscribing -----------------------------------------------------------------------------------------

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var messages = new List<string>();
            foreach (var m in mode is { } given ? [(int)given] : new[] { 1, 2, 3 }) UnsubscribeOne(target.Symbol, target.Exchange, m, messages);
            return messages;
        }
    }

    private void UnsubscribeOne(string symbol, string exchange, int mode, List<string> messages)
    {
        var key = KeyOf(symbol, exchange);
        if (!subscribed.TryGetValue(key, out var info)) return;
        if (!modes.TryGetValue(key, out var set)) return;
        set.Remove(mode);
        if (set.Count > 0) return;
        modes.Remove(key);
        subscribed.Remove(key);
        if (indexChannel.Contains(key))
        {
            messages.Add(Unsend("index", [info.SubName ?? symbol], info.NubraExchange ?? Exchanges.GetValueOrDefault(exchange, "NSE"), null));
            indexChannel.Remove(key);
        }

        if (bucketChannel.Contains(key))
        {
            messages.Add(Unsend("ohlcv", [info.SubName ?? symbol], info.NubraExchange ?? Exchanges.GetValueOrDefault(exchange, "NSE"), "1d"));
            bucketChannel.Remove(key);
            foreach (var name in indexNames.Where(p => p.Value == (symbol, exchange)).Select(p => p.Key).ToList()) indexNames.Remove(name);
        }

        if (info.RefId is { } refId)
        {
            messages.Add(Unsend("orderbook", [refId.ToString(CultureInfo.InvariantCulture)], null, null));
            refIds.Remove(refId);
            bookChannel.Remove(refId);
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return sent.Values.Select(b => Message("batch_subscribe", b.Kind, b.Names, b.Exchange, b.Interval)).ToList();
    }

    public void Reset()
    {
        lock (gate)
        {
            subscribed.Clear();
            indexNames.Clear();
            refIds.Clear();
            modes.Clear();
            indexChannel.Clear();
            bucketChannel.Clear();
            bookChannel.Clear();
            buckets.Clear();
            sent.Clear();
        }
    }

    // ---- frames -------------------------------------------------------------------------------------------------

    private static double Rupees(long paise) => paise != 0 ? paise / 100.0 : 0;

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var decoded = NubraFrames.Decode(frame);
        var output = new List<FeedPublish>();
        lock (gate)
        {
            foreach (var index in decoded.Indexes) OnIndex(index, nowMs, output);
            foreach (var book in decoded.Books) OnBook(book, nowMs, output);
            foreach (var bucket in decoded.Buckets) OnBucket(bucket);
        }

        return output;
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    private void OnIndex(NubraIndex index, long nowMs, List<FeedPublish> output)
    {
        if (index.Name.Length == 0) return;
        var upper = index.Name.ToUpperInvariant();
        string symbol;
        string? exchange = null;
        if (NubraCommands.IndexNames.TryGetValue(upper, out var mapped))
        {
            symbol = mapped;
            foreach (var candidate in new[] { "NSE_INDEX", "BSE_INDEX" })
                if (modes.ContainsKey(KeyOf(symbol, candidate)))
                {
                    exchange = candidate;
                    break;
                }

            if (exchange is null) return;
        }
        else
        {
            if (!indexNames.TryGetValue(upper, out var found)) return;
            (symbol, exchange) = found;
        }

        if (!modes.TryGetValue(KeyOf(symbol, exchange), out var live) || live.Count == 0) return;
        var set = live.ToHashSet();
        var ltp = Rupees(index.Value);
        var prevClose = Rupees(index.PrevClose);
        var timestamp = index.Timestamp != 0 ? index.Timestamp : nowMs;
        var hasBucket = buckets.TryGetValue(upper, out var bucket);
        if (set.Contains(2))
            output.Add(new FeedPublish($"{exchange}_{symbol}_QUOTE", new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = "quote", ["ltp"] = ltp, ["open"] = hasBucket ? bucket.Open : 0.0, ["high"] = Rupees(index.High), ["low"] = Rupees(index.Low), ["close"] = hasBucket ? bucket.Close : prevClose,
                ["volume"] = index.Volume, ["prev_close"] = prevClose, ["timestamp"] = timestamp,
            }));
        if (set.Contains(1))
            output.Add(new FeedPublish($"{exchange}_{symbol}_LTP", new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = "ltp", ["ltp"] = ltp, ["timestamp"] = timestamp }));
    }

    private void OnBucket(NubraBucket bucket)
    {
        if (bucket.Name.Length == 0) return;
        var upper = bucket.Name.ToUpperInvariant();
        var entry = (Open: Rupees(bucket.Open), Close: Rupees(bucket.Close));
        buckets[upper] = entry;
        if (NubraCommands.IndexNames.TryGetValue(upper, out var mapped)) buckets[mapped] = entry;
    }

    private static JsonArray Ladder(IReadOnlyList<NubraLevel> levels)
    {
        var rows = levels.Select(l => (Price: Rupees(l.Price), l.Quantity, l.Orders)).ToList();
        while (rows.Count < 5) rows.Add((0, 0, 0));
        return new JsonArray(rows.Take(5).Select(r => (JsonNode?)new JsonObject { ["price"] = r.Price, ["quantity"] = r.Quantity, ["orders"] = r.Orders }).ToArray());
    }

    private void OnBook(NubraBook book, long nowMs, List<FeedPublish> output)
    {
        var refId = book.RefId != 0 ? book.RefId : book.InstId;
        if (refId == 0 || !refIds.TryGetValue(refId, out var found)) return;
        var (symbol, exchange) = found;
        if (!modes.TryGetValue(KeyOf(symbol, exchange), out var live) || live.Count == 0) return;
        var set = live.ToHashSet();
        var ltp = Rupees(book.Ltp);
        var timestamp = book.Timestamp != 0 ? book.Timestamp : nowMs;
        if (set.Contains(1))
            output.Add(new FeedPublish($"{exchange}_{symbol}_LTP", new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = "ltp", ["ltp"] = ltp, ["timestamp"] = timestamp }));
        if (set.Contains(2))
            output.Add(new FeedPublish($"{exchange}_{symbol}_QUOTE", new JsonObject
            {
                ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = "quote", ["ltp"] = ltp, ["volume"] = book.Volume, ["bid"] = book.Bids.Count > 0 ? Rupees(book.Bids[0].Price) : 0.0, ["ask"] = book.Asks.Count > 0 ? Rupees(book.Asks[0].Price) : 0.0, ["timestamp"] = timestamp,
            }));
        if (!set.Contains(3)) return;
        var buy = Ladder(book.Bids);
        var sell = Ladder(book.Asks);
        output.Add(new FeedPublish($"{exchange}_{symbol}_DEPTH", new JsonObject
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = "full", ["ltp"] = ltp, ["volume"] = book.Volume, ["timestamp"] = timestamp, ["total_buy_quantity"] = buy.Sum(l => l!["quantity"]!.GetValue<long>()),
            ["total_sell_quantity"] = sell.Sum(l => l!["quantity"]!.GetValue<long>()), ["depth"] = new JsonObject { ["buy"] = buy, ["sell"] = sell },
        }));
    }
}
