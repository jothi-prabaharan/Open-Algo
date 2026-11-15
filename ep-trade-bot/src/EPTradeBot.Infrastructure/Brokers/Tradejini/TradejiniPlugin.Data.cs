using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Tradejini;

/// <summary>
/// Tradejini quotes, depth and several quotes (broker/tradejini api/data.py): there is no quote call over REST, so each read opens a short-lived stream connection with the token, subscribes the scrip (L1 for a quote, L5 for depth), reads what
/// arrives and closes it. A quote is returned as soon as a last price record holds every field a quote reads (and the open interest on derivatives); after a second without that, whatever last price record there is is returned as it stands, and with none
/// at all for forty such seconds the read has failed. Depth waits twenty seconds for any depth packet and returns its first five levels a side. Several quotes go a hundred to a batch on one connection, each batch waiting up to as many twentieths of a second as it has symbols (two seconds at
/// least, ten at most) for all of them to be complete. OpenAlgo waits three seconds after connecting before it asks, and answers a read that failed with zeros; EP Trade bot asks at once and says the read failed.
/// </summary>
public sealed partial class TradejiniPlugin : IBrokerMultiQuotes
{
    private const int QuoteBatch = 100;
    private const int QuoteSteps = 40;
    private static readonly TimeSpan QuoteStep = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DepthWait = TimeSpan.FromSeconds(20);
    private static readonly string[] L1Fields = ["ltp", "open", "high", "low", "close", "vol", "bidPrice", "askPrice"];
    private static readonly HashSet<string> OiExchanges = ["NFO", "BFO", "CDS", "BCD", "MCX"];

    private static ITradejiniSockets? defaultSockets;

    private ITradejiniSockets Sockets => sockets ?? (defaultSockets ??= new TradejiniSocketFactory());

    private Uri StreamAddress(BrokerSession session)
    {
        var key = ApiKey(session);
        var token = session.AuthToken.Contains(':', StringComparison.Ordinal) ? session.AuthToken : key is not null ? $"{key}:{session.AuthToken}" : throw new BrokerException(KeyMissing, 409);
        return new Uri($"{sessions.FeedUrl(Name) ?? FeedAddress}?token={token}&version=3.1");
    }

    private async Task<ITradejiniStream> OpenAsync(BrokerSession session, CancellationToken cancellationToken) =>
        await Sockets.OpenAsync(StreamAddress(session), cancellationToken) ?? throw new BrokerException($"{Display} market data could not be reached. Try again in a moment.", 502);

    private static string Request(string feed, IEnumerable<string> tokens) =>
        new JsonObject { ["type"] = feed, ["action"] = "sub", ["tokens"] = new JsonArray(tokens.Select(t => (JsonNode?)new JsonObject { ["t"] = t }).ToArray()) }.ToJsonString() + "\n";

    private static string StreamKey(SymbolRecord record) => $"{record.Token}_{record.Exchange.Replace("_INDEX", "", StringComparison.Ordinal)}";

    private static bool IsComplete(Dictionary<string, JsonNode?>? quote, string exchange) =>
        quote is not null && L1Fields.All(quote.ContainsKey) && (!OiExchanges.Contains(exchange) || quote.ContainsKey("OI"));

    private static double Float(Dictionary<string, JsonNode?> data, string key) => data.TryGetValue(key, out var v) && BookFormatting.Number(v) is { } n ? n : 0;

    private static long Whole(Dictionary<string, JsonNode?> data, string key) => data.TryGetValue(key, out var v) && BookFormatting.Number(v) is { } n ? (long)Math.Truncate(n) : 0;

    private static JsonObject FormatQuote(Dictionary<string, JsonNode?> data) => new()
    {
        ["ask"] = Float(data, "askPrice"), ["bid"] = Float(data, "bidPrice"), ["high"] = Float(data, "high"), ["low"] = Float(data, "low"), ["ltp"] = Float(data, "ltp"), ["open"] = Float(data, "open"),
        ["prev_close"] = Float(data, "close"), ["volume"] = Whole(data, "vol"), ["oi"] = Whole(data, "OI"),
    };

    /// <summary>The last price record of a scrip, looked for under the keys OpenAlgo tries in the order it tries them.</summary>
    private static Dictionary<string, JsonNode?>? Cached(Dictionary<string, Dictionary<string, JsonNode?>> l1, string token, string wsExchange, bool exchangeFirst)
    {
        var keys = exchangeFirst
            ? new[] { $"{token}_{wsExchange}", $"{wsExchange}_{token}", $"{token}_NSE", $"{token}_BSE", token }
            : new[] { $"{token}_{wsExchange}", $"{token}_NSE", $"{token}_BSE", token, $"{wsExchange}_{token}" };
        foreach (var key in keys)
            if (l1.TryGetValue(key, out var found)) return found;
        return null;
    }

    private static void Take(TradejiniPacketReader reader, byte[] frame, Dictionary<string, Dictionary<string, JsonNode?>> l1, Dictionary<string, Dictionary<string, JsonNode?>> l5, ref Dictionary<string, JsonNode?>? lastQuote, ref Dictionary<string, JsonNode?>? lastDepth, List<TradejiniMessage> depthMessages)
    {
        foreach (var message in reader.Read(frame))
        {
            var symbol = message.Data.TryGetValue("symbol", out var s) ? s!.GetValue<string>() : "";
            if (message.Type == "L1")
            {
                l1[symbol] = message.Data;
                lastQuote = message.Data;
            }
            else if (message.Type == "L5")
            {
                l5[symbol] = message.Data;
                lastDepth = message.Data;
                depthMessages.Add(message);
            }
        }
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);
        await using var stream = await OpenAsync(session, cancellationToken);
        var reader = new TradejiniPacketReader();
        var l1 = new Dictionary<string, Dictionary<string, JsonNode?>>();
        var l5 = new Dictionary<string, Dictionary<string, JsonNode?>>();
        Dictionary<string, JsonNode?>? lastQuote = null, lastDepth = null;
        var depth = new List<TradejiniMessage>();
        var wsExchange = exchange.Replace("_INDEX", "", StringComparison.Ordinal);
        var key = StreamKey(record);
        await stream.SendAsync(Request("L1", [key]), cancellationToken);

        Dictionary<string, JsonNode?>? Found()
        {
            var cached = Cached(l1, record.Token, wsExchange, exchangeFirst: false);
            if (cached is not null) return cached;
            return lastQuote is not null && lastQuote.TryGetValue("symbol", out var s) && Cached(new() { [s!.GetValue<string>()] = lastQuote }, record.Token, wsExchange, exchangeFirst: false) is { } match ? match : null;
        }

        for (var step = 0; step < QuoteSteps; step++)
        {
            var clock = Stopwatch.StartNew();
            while (!IsComplete(Found(), exchange))
            {
                var remaining = QuoteStep - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                var frame = await stream.ReceiveAsync(remaining, cancellationToken);
                if (frame is null) break;
                Take(reader, frame, l1, l5, ref lastQuote, ref lastDepth, depth);
            }

            if (Found() is { } quote) return FormatQuote(quote);
        }

        throw new BrokerException($"{Display} did not send a quote for {symbol} on {exchange}. Try again in a moment.", 500);
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);
        await using var stream = await OpenAsync(session, cancellationToken);
        var reader = new TradejiniPacketReader();
        var l1 = new Dictionary<string, Dictionary<string, JsonNode?>>();
        var l5 = new Dictionary<string, Dictionary<string, JsonNode?>>();
        Dictionary<string, JsonNode?>? lastQuote = null, lastDepth = null;
        var depth = new List<TradejiniMessage>();
        await stream.SendAsync(Request("L5", [StreamKey(record)]), cancellationToken);
        var clock = Stopwatch.StartNew();
        while (depth.Count == 0)
        {
            var remaining = DepthWait - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var frame = await stream.ReceiveAsync(remaining, cancellationToken);
            if (frame is null) break;
            Take(reader, frame, l1, l5, ref lastQuote, ref lastDepth, depth);
        }

        // The depth for the scrip asked for, else the last depth that arrived, whatever scrip it was for.
        var message = depth.LastOrDefault(m => m.Data.TryGetValue("symbol", out var s) && s!.GetValue<string>() == StreamKey(record)) ?? depth.LastOrDefault()
            ?? throw new BrokerException($"{Display} did not send the market depth for {symbol} on {exchange}. Try again in a moment.", 500);

        JsonArray Side(List<Dictionary<string, JsonNode?>> levels)
        {
            var rows = new JsonArray();
            foreach (var level in levels.Take(5)) rows.Add(new JsonObject { ["price"] = Float(level, "price"), ["quantity"] = Whole(level, "qty") });
            while (rows.Count < 5) rows.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0L });
            return rows;
        }

        var bids = Side(message.Bids);
        var asks = Side(message.Asks);
        return new JsonObject
        {
            ["asks"] = asks, ["bids"] = bids, ["high"] = Float(message.Data, "high"), ["low"] = Float(message.Data, "low"), ["ltp"] = Float(message.Data, "ltp"), ["ltq"] = Whole(message.Data, "ltq"), ["oi"] = Whole(message.Data, "OI"),
            ["open"] = Float(message.Data, "open"), ["prev_close"] = Float(message.Data, "close"), ["totalbuyqty"] = bids.Sum(b => (long)b!["quantity"]!), ["totalsellqty"] = asks.Sum(a => (long)a!["quantity"]!), ["volume"] = Whole(message.Data, "vol"),
        };
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        if (instruments.Count == 0) return results;
        await using var stream = await OpenAsync(session, cancellationToken);
        var reader = new TradejiniPacketReader();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            var batch = instruments.Skip(start).Take(QuoteBatch).ToList();
            var l1 = new Dictionary<string, Dictionary<string, JsonNode?>>();
            var l5 = new Dictionary<string, Dictionary<string, JsonNode?>>();
            Dictionary<string, JsonNode?>? lastQuote = null, lastDepth = null;
            var depth = new List<TradejiniMessage>();
            // One entry for each stream key: a scrip asked for twice is one entry, as it is for OpenAlgo.
            var entries = new OrderedDictionary<string, SymbolRecord>();
            foreach (var record in batch) entries[StreamKey(record)] = record;
            await stream.SendAsync(Request("L1", batch.Select(StreamKey)), cancellationToken);

            Dictionary<string, JsonNode?>? For(KeyValuePair<string, SymbolRecord> entry) =>
                Cached(l1, entry.Value.Token, entry.Value.Exchange.Replace("_INDEX", "", StringComparison.Ordinal), exchangeFirst: true);

            var wait = TimeSpan.FromSeconds(Math.Min(Math.Max(entries.Count * 0.05, 2), 10));
            var clock = Stopwatch.StartNew();
            while (!entries.All(e => IsComplete(For(e), e.Value.Exchange)))
            {
                var remaining = wait - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                var frame = await stream.ReceiveAsync(remaining, cancellationToken);
                if (frame is null) break;
                Take(reader, frame, l1, l5, ref lastQuote, ref lastDepth, depth);
            }

            foreach (var entry in entries)
            {
                if (For(entry) is { } quote)
                {
                    results.Add(new JsonObject
                    {
                        ["symbol"] = entry.Value.Symbol, ["exchange"] = entry.Value.Exchange,
                        ["data"] = new JsonObject
                        {
                            ["bid"] = Float(quote, "bidPrice"), ["ask"] = Float(quote, "askPrice"), ["open"] = Float(quote, "open"), ["high"] = Float(quote, "high"), ["low"] = Float(quote, "low"), ["ltp"] = Float(quote, "ltp"),
                            ["prev_close"] = Float(quote, "close"), ["volume"] = Whole(quote, "vol"), ["oi"] = Whole(quote, "OI"),
                        },
                    });
                }
                else
                {
                    results.Add(new JsonObject { ["symbol"] = entry.Value.Symbol, ["exchange"] = entry.Value.Exchange, ["error"] = "No data received" });
                }
            }
        }

        return results;
    }
}
