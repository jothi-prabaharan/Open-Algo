using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

/// <summary>
/// Motilal's depth and several quotes (broker/motilal api/data.py get_depth, get_multiquotes): nothing is asked for over REST; the scrips are registered on the broadcast feed, the packets are read until each one has what the read uses (or the wait
/// is up) and the scrips are unregistered. OpenAlgo keeps one feed socket for the session; here each read opens a short one.
/// </summary>
public sealed partial class MotilalPlugin
{
    private const int BatchSize = 100;
    private static readonly TimeSpan DepthWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IndexWait = TimeSpan.FromSeconds(3);

    private Uri FeedUri() => new(sessions.FeedUrl(Name) ?? "wss://ws1feed.motilaloswal.com/jwebsocket/jwebsocket");

    private static string Segment(string exchange) => exchange is "NFO" or "BFO" or "CDS" or "MCX" ? "DERIVATIVES" : "CASH";

    private static int ScripCode(string token) => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : throw new BrokerException($"The token {token} is not a number.", 400);

    private async Task RunFeedAsync(BrokerSession session, IReadOnlyList<MotilalMessage> subscribes, IReadOnlyList<MotilalMessage> unsubscribes, Func<byte[], bool> onFrame, TimeSpan wait, string what, CancellationToken cancellationToken)
    {
        var login = MotilalBook.LoginPacket(session.ClientId ?? "");
        if (!await Sockets.RunAsync(FeedUri(), login, subscribes, unsubscribes, onFrame, wait, cancellationToken))
            throw new BrokerException($"{Display} did not answer on its live price connection, so {what} could not be read. Try again in a moment.", 502);
    }

    private static JsonArray Levels(IReadOnlyList<MotilalLevel> levels)
    {
        var rows = new JsonArray();
        for (var i = 0; i < 5; i++) rows.Add(i < levels.Count ? new JsonObject { ["price"] = levels[i].Price, ["quantity"] = levels[i].Quantity } : new JsonObject { ["price"] = 0, ["quantity"] = 0 });
        return rows;
    }

    private static double Of(IReadOnlyDictionary<string, double>? quote, string name) => quote is not null && quote.TryGetValue(name, out var v) ? v : 0.0;

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var info = await InstrumentAsync(symbol, exchange, cancellationToken);
        var book = new MotilalBook();
        if (IndexExchanges.Contains(exchange)) return await IndexDepthAsync(session, book, info, symbol, exchange, cancellationToken);
        var motilal = MotilalExchange(exchange);
        var scrip = ScripCode(info.Token);
        var segment = Segment(exchange);
        book.Register(motilal, scrip);
        await RunFeedAsync(session, [MotilalMessage.Of(MotilalBook.RegisterPacket(motilal, segment, scrip, true))], [MotilalMessage.Of(MotilalBook.RegisterPacket(motilal, segment, scrip, false))],
            frame =>
            {
                book.Feed(frame);
                return book.HasSnapshot(motilal, scrip, segment == "DERIVATIVES", true);
            }, DepthWait, $"the depth of {symbol}", cancellationToken);
        var depth = book.GetDepth(motilal, scrip);
        var quote = book.GetQuote(motilal, scrip);
        book.Unregister(motilal, scrip);
        return new JsonObject
        {
            ["bids"] = Levels(depth?.Bids ?? []), ["asks"] = Levels(depth?.Asks ?? []), ["high"] = Of(quote, "high"), ["low"] = Of(quote, "low"), ["ltp"] = Of(quote, "ltp"), ["ltq"] = 0, ["open"] = Of(quote, "open"),
            ["prev_close"] = Of(quote, "prev_close"), ["volume"] = Of(quote, "volume"), ["oi"] = (long)Of(quote, "open_interest"), ["totalbuyqty"] = 0, ["totalsellqty"] = 0,
        };
    }

    private async Task<JsonObject> IndexDepthAsync(BrokerSession session, MotilalBook book, SymbolRecord info, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var motilal = exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => null };
        if (motilal is null)
        {
            await RunFeedAsync(session, [], [], _ => true, TimeSpan.Zero, $"the depth of {symbol}", cancellationToken);
            throw new BrokerException($"{Display} gives no depth for {symbol}: it can stream NSE and BSE indices only.", 400);
        }

        var client = session.ClientId ?? "";
        await RunFeedAsync(session, [MotilalMessage.Of(MotilalBook.IndexFrame(client, motilal, true))], [MotilalMessage.Of(MotilalBook.IndexFrame(client, motilal, false))], frame =>
        {
            book.Feed(frame);
            return false;
        }, IndexWait, $"the depth of {symbol}", cancellationToken);
        var quote = book.GetQuote(motilal, info.Token);
        var rate = book.GetIndexRate(motilal, info.Token);
        var ltp = rate is { } r && r != 0 ? r : Of(quote, "ltp");
        if (ltp == 0) throw new BrokerException($"{Display} sent no value for {symbol} yet. Try again in a moment.", 502);
        return new JsonObject
        {
            ["bids"] = Levels([]), ["asks"] = Levels([]), ["high"] = Of(quote, "high"), ["low"] = Of(quote, "low"), ["ltp"] = ltp, ["ltq"] = 0, ["open"] = Of(quote, "open"), ["prev_close"] = Of(quote, "prev_close"), ["volume"] = 0,
            ["oi"] = 0, ["totalbuyqty"] = 0, ["totalsellqty"] = 0,
        };
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        for (var at = 0; at < instruments.Count; at += BatchSize)
        {
            foreach (var item in await BatchAsync(session, instruments.Skip(at).Take(BatchSize).ToList(), cancellationToken)) all.Add(item);
            if (at + BatchSize < instruments.Count) await Task.Delay(TimeSpan.FromMilliseconds(100), clock, cancellationToken);
        }

        return all;
    }

    private sealed record Scrip(string Exchange, string Segment, int Code, string Symbol, string OpenAlgoExchange, string Token);

    private async Task<List<JsonNode>> BatchAsync(BrokerSession session, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var skipped = new List<JsonNode>();
        var scrips = new Dictionary<string, Scrip>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, Scrip>(StringComparer.Ordinal);
        var registered = new List<Scrip>();
        var indexExchanges = new List<string>();
        var book = new MotilalBook();
        var client = session.ClientId ?? "";
        var subscribes = new List<MotilalMessage>();
        foreach (var info in batch)
        {
            var exchange = info.Exchange;
            if (string.IsNullOrEmpty(info.Token))
            {
                skipped.Add(new JsonObject { ["symbol"] = info.Symbol, ["exchange"] = exchange, ["data"] = null, ["error"] = "Could not resolve token" });
                continue;
            }

            if (IndexExchanges.Contains(exchange))
            {
                var index = exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => null };
                if (index is null)
                {
                    skipped.Add(new JsonObject { ["symbol"] = info.Symbol, ["exchange"] = exchange, ["data"] = null, ["error"] = "IndexRegister supports NSE and BSE indices only" });
                    continue;
                }

                if (!indexExchanges.Contains(index))
                {
                    indexExchanges.Add(index);
                    subscribes.Add(MotilalMessage.Of(MotilalBook.IndexFrame(client, index, true)));
                }

                indexes[$"{index}:{info.Token}"] = new Scrip(index, "", 0, info.Symbol, exchange, info.Token);
                continue;
            }

            var motilal = MotilalExchange(exchange);
            var code = ScripCode(info.Token);
            var segment = Segment(exchange);
            book.Register(motilal, code);
            subscribes.Add(MotilalMessage.Of(MotilalBook.RegisterPacket(motilal, segment, code, true)));
            var scrip = new Scrip(motilal, segment, code, info.Symbol, exchange, info.Token);
            registered.Add(scrip);
            scrips[$"{motilal}:{info.Token}"] = scrip;
        }

        var unsubscribes = registered.Select(s => MotilalMessage.Of(MotilalBook.RegisterPacket(s.Exchange, s.Segment, s.Code, false))).Concat(indexExchanges.Select(e => MotilalMessage.Of(MotilalBook.IndexFrame(client, e, false)))).ToList();
        var pending = registered.Count + indexes.Count;
        var wait = pending == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(Math.Max(pending * 0.1, 2), 5));
        await RunFeedAsync(session, subscribes, unsubscribes, frame =>
        {
            book.Feed(frame);
            return indexes.Count == 0 && registered.All(s => book.HasSnapshot(s.Exchange, s.Code, s.Segment == "DERIVATIVES", false));
        }, wait, "the quotes", cancellationToken);
        if (pending == 0) return skipped;

        var results = new List<JsonNode>();
        foreach (var scrip in scrips.Values)
        {
            var quote = book.GetQuote(scrip.Exchange, scrip.Token);
            if (quote is null)
            {
                results.Add(new JsonObject { ["symbol"] = scrip.Symbol, ["exchange"] = scrip.OpenAlgoExchange, ["error"] = "No data received" });
                continue;
            }

            var depth = book.GetDepth(scrip.Exchange, scrip.Token);
            double First(IReadOnlyList<MotilalLevel>? levels) => levels?.FirstOrDefault(l => l.Price != 0)?.Price ?? 0.0;
            results.Add(new JsonObject
            {
                ["symbol"] = scrip.Symbol, ["exchange"] = scrip.OpenAlgoExchange,
                ["data"] = new JsonObject
                {
                    ["bid"] = First(depth?.Bids), ["ask"] = First(depth?.Asks), ["open"] = Of(quote, "open"), ["high"] = Of(quote, "high"), ["low"] = Of(quote, "low"), ["ltp"] = Of(quote, "ltp"), ["prev_close"] = Of(quote, "prev_close"),
                    ["volume"] = (long)Of(quote, "volume"), ["oi"] = (long)Of(quote, "open_interest"),
                },
            });
        }

        foreach (var index in indexes.Values)
        {
            var rate = book.GetIndexRate(index.Exchange, index.Token);
            var quote = book.GetQuote(index.Exchange, index.Token);
            var ltp = rate is { } r && r != 0 ? r : Of(quote, "ltp");
            if (ltp != 0 || quote is { Count: > 0 })
                results.Add(new JsonObject
                {
                    ["symbol"] = index.Symbol, ["exchange"] = index.OpenAlgoExchange,
                    ["data"] = new JsonObject { ["bid"] = 0.0, ["ask"] = 0.0, ["open"] = Of(quote, "open"), ["high"] = Of(quote, "high"), ["low"] = Of(quote, "low"), ["ltp"] = ltp, ["prev_close"] = Of(quote, "prev_close"), ["volume"] = 0, ["oi"] = 0 },
                });
            else results.Add(new JsonObject { ["symbol"] = index.Symbol, ["exchange"] = index.OpenAlgoExchange, ["error"] = "No data received" });
        }

        skipped.AddRange(results);
        return skipped;
    }
}
