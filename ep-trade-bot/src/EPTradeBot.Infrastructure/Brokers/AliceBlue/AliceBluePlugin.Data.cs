using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.AliceBlue;

/// <summary>
/// AliceBlue market data (broker/aliceblue api/data.py). AliceBlue has no quote call over REST: a market-data socket is subscribed to the instruments, what arrives is merged into a quote or a depth for each, and the subscription is given
/// back (see <see cref="AliceBlueMarketSocket"/>). A single quote is asked for twice, on a new socket the second time. Several quotes go a hundred to a batch and wait for every instrument, up to two seconds and more for a long batch, then three
/// more for any that are missing. A read returns as soon as what it waits for has arrived, where OpenAlgo always sleeps the full two seconds for a single quote or a depth.
/// </summary>
public sealed partial class AliceBluePlugin : IBrokerMultiQuotes
{
    private const int QuoteBatch = 100;
    private IAliceBlueSockets? defaultSockets;

    private IAliceBlueSockets Sockets => sockets ?? (defaultSockets ??= new AliceBlueSocketPool(CreateSocketSessionAsync, () => sessions.FeedUrl(Name), clock));

    /// <summary>Ends the last socket session and makes a new one, as the feed does before it connects.</summary>
    private async Task<bool> CreateSocketSessionAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        try
        {
            var body = new JsonObject { ["source"] = "API", ["userId"] = session.ClientId };
            await SendAsync(session, HttpMethod.Post, "/open-api/od/v1/profile/invalidateWsSess", body, read: false, cancellationToken);
            var (_, reply) = await SendAsync(session, HttpMethod.Post, "/open-api/od/v1/profile/createWsSess", body.DeepClone(), read: false, cancellationToken);
            return reply is JsonObject answer && Str(answer["status"]) == "Ok";
        }
        catch (BrokerException)
        {
            return false;
        }
    }

    private static string ApiExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", "MCX_INDEX" => "MCX", _ => exchange };

    /// <summary>The token as a whole number: 3045.0 is 3045.</summary>
    private static string WholeToken(string token) => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? ((long)Math.Truncate(d)).ToString(CultureInfo.InvariantCulture) : token;

    private TimeSpan Wait(TimeSpan span) => sessions.RetryBackoff(Name, span);

    /// <summary>Waits until <paramref name="ready"/> is true, for at most <paramref name="span"/>, looking every fifty milliseconds.</summary>
    private async Task UntilAsync(Func<bool> ready, TimeSpan span, CancellationToken cancellationToken)
    {
        var limit = Wait(span);
        var started = DateTime.UtcNow;
        while (!ready() && DateTime.UtcNow - started < limit) await Task.Delay(50, cancellationToken);
    }

    private async Task<AliceBlueMarketSocket?> ConnectedAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var socket = await Sockets.GetAsync(session, false, cancellationToken);
        if (socket is null || !socket.Connected) socket = await Sockets.GetAsync(session, true, cancellationToken);
        return socket is { Connected: true } ? socket : null;
    }

    private async Task<SymbolRecord> RecordAsync(string symbol, string exchange, CancellationToken cancellationToken) =>
        await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
        ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);

    // ---- one quote ---------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await RecordAsync(symbol, exchange, cancellationToken);
        var instrument = new MarketInstrument(ApiExchange(exchange), WholeToken(record.Token));
        JsonObject? quote = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            quote = await TryQuoteAsync(session, instrument, cancellationToken);
            if (quote is not null) break;
            if (attempt < 2)
            {
                await Sockets.GetAsync(session, true, cancellationToken);
                await Task.Delay(Wait(TimeSpan.FromSeconds(1)), cancellationToken);
            }
        }

        if (quote is null) throw new BrokerException($"{Display} did not send a quote for {symbol} on {exchange}. Try again in a moment.", 500);
        return Formatted(quote);
    }

    private async Task<JsonObject?> TryQuoteAsync(BrokerSession session, MarketInstrument instrument, CancellationToken cancellationToken)
    {
        var socket = await ConnectedAsync(session, cancellationToken);
        if (socket is null) return null;
        if (!socket.Subscribe([instrument], depth: false)) return null;
        try
        {
            await UntilAsync(() => socket.GetQuote(instrument.Exchange, instrument.Token) is not null, TimeSpan.FromSeconds(2), cancellationToken);
            return socket.GetQuote(instrument.Exchange, instrument.Token);
        }
        finally
        {
            socket.Unsubscribe([instrument]);
        }
    }

    private static double Number(JsonObject quote, string name) => quote.TryGetPropertyValue(name, out var node) && node is not null ? Float(node) : 0;

    private static JsonObject Formatted(JsonObject quote) => new()
    {
        ["bid"] = Number(quote, "bid"), ["ask"] = Number(quote, "ask"), ["open"] = Number(quote, "open"), ["high"] = Number(quote, "high"), ["low"] = Number(quote, "low"), ["ltp"] = Number(quote, "ltp"),
        ["prev_close"] = Number(quote, "close"), ["volume"] = (long)Number(quote, "volume"), ["oi"] = (long)Number(quote, "open_interest"),
    };

    // ---- depth ---------------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await RecordAsync(symbol, exchange, cancellationToken);
        var instrument = new MarketInstrument(ApiExchange(exchange), WholeToken(record.Token));
        var socket = await ConnectedAsync(session, cancellationToken) ?? throw new BrokerException($"{Display} market data could not be reached. Check your connection, then try again.", 502);
        if (!socket.Subscribe([instrument], depth: true)) throw new BrokerException($"{Display} would not take the market depth request for {symbol} on {exchange}. Try again in a moment.", 502);
        JsonObject? depth;
        try
        {
            await UntilAsync(() => socket.GetDepth(instrument.Exchange, instrument.Token) is not null, TimeSpan.FromSeconds(2), cancellationToken);
            depth = socket.GetDepth(instrument.Exchange, instrument.Token);
        }
        finally
        {
            socket.Unsubscribe([instrument]);
        }

        if (depth is null) throw new BrokerException($"{Display} did not send the market depth for {symbol} on {exchange}. Try again in a moment.", 500);
        JsonArray Side(string name)
        {
            var levels = (depth[name] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var side = new JsonArray();
            for (var i = 0; i < 5; i++) side.Add(i < levels.Count ? new JsonObject { ["price"] = levels[i]["price"]?.DeepClone() ?? 0, ["quantity"] = levels[i]["quantity"]?.DeepClone() ?? 0 } : new JsonObject { ["price"] = 0, ["quantity"] = 0 });
            return side;
        }

        return new JsonObject
        {
            ["bids"] = Side("bids"), ["asks"] = Side("asks"), ["high"] = Number(depth, "high"), ["low"] = Number(depth, "low"), ["ltp"] = Number(depth, "ltp"), ["ltq"] = (long)Number(depth, "last_trade_quantity"),
            ["open"] = Number(depth, "open"), ["prev_close"] = Number(depth, "close"), ["volume"] = (long)Number(depth, "volume"), ["oi"] = (long)Number(depth, "open_interest"),
            ["totalbuyqty"] = (long)Number(depth, "total_buy_quantity"), ["totalsellqty"] = (long)Number(depth, "total_sell_quantity"),
        };
    }

    // ---- several quotes ------------------------------------------------------------------------------------------

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        for (var start = 0; start < instruments.Count; start += QuoteBatch)
        {
            foreach (var item in await BatchAsync(session, instruments.Skip(start).Take(QuoteBatch).ToList(), cancellationToken)) all.Add(item);
        }

        return all;
    }

    private async Task<List<JsonNode>> BatchAsync(BrokerSession session, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var results = new List<JsonNode>();
        var socket = await ConnectedAsync(session, cancellationToken) ?? throw new BrokerException($"{Display} market data could not be reached. Check your connection, then try again.", 502);
        var wanted = new List<MarketInstrument>();
        var names = new OrderedDictionary<string, (string Symbol, string Exchange)>();
        foreach (var r in batch)
        {
            var instrument = new MarketInstrument(ApiExchange(r.Exchange), WholeToken(r.Token));
            wanted.Add(instrument);
            names[$"{instrument.Exchange}:{instrument.Token}"] = (r.Symbol, r.Exchange);
        }

        var subscribed = socket.Subscribe(wanted, depth: false);
        if (!subscribed)
        {
            socket = await Sockets.GetAsync(session, true, cancellationToken);
            subscribed = socket is { Connected: true } && socket.Subscribe(wanted, depth: false);
        }

        if (!subscribed)
        {
            foreach (var (_, name) in names) results.Add(new JsonObject { ["symbol"] = name.Symbol, ["exchange"] = name.Exchange, ["error"] = "Subscription failed" });
            return results;
        }

        try
        {
            bool Has(string key) => socket!.GetQuote(key.Split(':')[0], key.Split(':')[1]) is not null;
            await UntilAsync(() => names.Keys.All(Has), TimeSpan.FromSeconds(Math.Min(Math.Max(wanted.Count * 0.08, 2), 20)), cancellationToken);
            var missing = new List<string>();
            foreach (var (key, name) in names)
            {
                if (socket!.GetQuote(key.Split(':')[0], key.Split(':')[1]) is { } quote) results.Add(Item(name, Formatted(quote)));
                else missing.Add(key);
            }

            if (missing.Count > 0)
            {
                await UntilAsync(() => missing.All(Has), TimeSpan.FromSeconds(3), cancellationToken);
                foreach (var key in missing)
                {
                    var name = names[key];
                    results.Add(socket!.GetQuote(key.Split(':')[0], key.Split(':')[1]) is { } quote ? Item(name, Formatted(quote)) : new JsonObject { ["symbol"] = name.Symbol, ["exchange"] = name.Exchange, ["error"] = "No data received" });
                }
            }

            return results;
        }
        finally
        {
            socket!.Unsubscribe(wanted);
        }
    }

    private static JsonObject Item((string Symbol, string Exchange) name, JsonObject data) => new() { ["symbol"] = name.Symbol, ["exchange"] = name.Exchange, ["data"] = data };
}
