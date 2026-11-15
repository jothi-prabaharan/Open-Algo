using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>
/// Nubra's quotes, depth and several quotes (broker/nubra/api/data.py). A feed connection is opened for the read (<see cref="INubraSockets"/>); text commands subscribe to the channels, the protobuf frames that come back fill a cache, and the cache is looked at every
/// fifty milliseconds. A stock's quote waits up to two seconds for its price on the index channel, then takes its order book (with the open interest from the greeks channel), then asks GET /orderbooks/&lt;ref id&gt;?levels=1, then gives zeros. An index's quote is its candle bucket's
/// and is never asked of REST. Depth waits up to ten half-seconds for a full book, then asks ?levels=5; an index has none and gives zeros. Several quotes subscribe every book at once and wait one two-second window. Prices on the feed and over REST are in paise.
/// </summary>
public sealed partial class NubraPlugin : IBrokerMultiQuotes
{
    private const string FeedAddress = "wss://api.nubra.io/apibatch/ws";
    private const double TickSeconds = 0.05;
    private const double QuoteWaitSeconds = 2.0;
    private const double DepthStepSeconds = 0.5;
    private const int DepthSteps = 10;
    private const int FanOut = 5;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Dictionary<string, string> IndexNames = new()
    {
        ["NIFTY 50"] = "NIFTY", ["NIFTY BANK"] = "BANKNIFTY", ["NIFTY FINANCIAL SERVICES"] = "FINNIFTY", ["BSE SENSEX"] = "SENSEX", ["BSE SENSEX 50"] = "SENSEX50",
    };

    private static INubraSockets? defaultSockets;

    private INubraSockets Sockets => sockets ?? (defaultSockets ??= new NubraSnapshotSocket());

    // ---- what the feed has said -------------------------------------------------------------------------------

    private sealed class QuoteCache
    {
        public double Ltp, High, Low, PrevClose, Open, Close;
        public long Volume, VolumeOi;
        public bool HasIndex;
    }

    private sealed class BookCache
    {
        public double Ltp;
        public long Ltq, Volume, TotalBuy, TotalSell;
        public (double Price, long Quantity)[]? Bids, Asks;
        public long? Oi;
    }

    private static double Rupees(long paise) => paise != 0 ? paise / 100.0 : 0;

    /// <summary>A feed connection with the caches its frames fill, as OpenAlgo's NubraWebSocket keeps them.</summary>
    private sealed class FeedRead(INubraConnection connection, string token)
    {
        private readonly Dictionary<(string, string), QuoteCache> quotes = [];
        private readonly Dictionary<long, BookCache> books = [];

        public string Token => token;

        public async Task<bool> SendAsync(string text, CancellationToken cancellationToken) => await connection.SendAsync(text, cancellationToken);

        public async Task TickAsync(CancellationToken cancellationToken)
        {
            foreach (var raw in await connection.TickAsync(cancellationToken)) Apply(NubraFrames.Decode(raw));
        }

        public QuoteCache? Quote(string exchange, string symbol) => quotes.GetValueOrDefault((exchange, symbol.ToUpperInvariant()));

        public BookCache? Book(long refId) => books.GetValueOrDefault(refId);

        private static (string Exchange, string Name)? Key(string exchange, string name)
        {
            if (name.Length == 0) return null;
            var upper = name.ToUpperInvariant();
            return (exchange.Length > 0 ? exchange : "NSE", IndexNames.GetValueOrDefault(upper, upper));
        }

        private void Apply(NubraFrame frame)
        {
            foreach (var index in frame.Indexes)
            {
                if (Key(index.Exchange, index.Name) is not { } key) continue;
                var existing = quotes.GetValueOrDefault(key);
                quotes[key] = new QuoteCache
                {
                    Ltp = Rupees(index.Value), High = Rupees(index.High), Low = Rupees(index.Low), Volume = index.Volume, PrevClose = Rupees(index.PrevClose), VolumeOi = index.VolumeOi,
                    Open = existing?.Open ?? 0, Close = existing?.Close ?? 0, HasIndex = true,
                };
            }

            foreach (var bucket in frame.Buckets)
            {
                if (Key(bucket.Exchange, bucket.Name) is not { } key) continue;
                var entry = quotes.GetValueOrDefault(key) ?? new QuoteCache();
                var close = Rupees(bucket.Close);
                entry.Open = bucket.Open != 0 ? bucket.Open / 100.0 : entry.Open;
                entry.Close = close != 0 ? close : entry.Close;
                if (!entry.HasIndex)
                {
                    if (close != 0) entry.Ltp = close;
                    if (bucket.High != 0) entry.High = bucket.High / 100.0;
                    if (bucket.Low != 0) entry.Low = bucket.Low / 100.0;
                    entry.Volume = bucket.CumulativeVolume != 0 ? bucket.CumulativeVolume : bucket.BucketVolume;
                }

                quotes[key] = entry;
            }

            foreach (var book in frame.Books)
            {
                var refId = book.RefId != 0 ? book.RefId : book.InstId;
                if (refId == 0) continue;
                var bids = Levels(book.Bids);
                var asks = Levels(book.Asks);
                var entry = books.GetValueOrDefault(refId) ?? new BookCache();
                entry.Ltp = Rupees(book.Ltp);
                entry.Ltq = book.Ltq;
                entry.Volume = book.Volume;
                entry.TotalBuy = bids.Sum(b => b.Quantity);
                entry.TotalSell = asks.Sum(a => a.Quantity);
                entry.Bids = bids.Take(5).ToArray();
                entry.Asks = asks.Take(5).ToArray();
                books[refId] = entry;
            }

            foreach (var item in frame.Greeks)
            {
                if (item.RefId == 0) continue;
                var entry = books.GetValueOrDefault(item.RefId) ?? new BookCache();
                entry.Oi = item.Oi;
                if (entry.Ltp == 0) entry.Ltp = Rupees(item.Ltp);
                if (entry.Volume == 0) entry.Volume = item.Volume;
                books[item.RefId] = entry;
            }
        }

        private static List<(double Price, long Quantity)> Levels(IReadOnlyList<NubraLevel> levels)
        {
            var rows = levels.Select(l => (Rupees(l.Price), l.Quantity)).ToList();
            while (rows.Count < 5) rows.Add((0, 0));
            return rows;
        }
    }

    // ---- connection and commands ------------------------------------------------------------------------------

    private async Task<(FeedRead? Read, INubraConnection? Connection)> OpenFeedAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        var connection = await Sockets.OpenAsync(new Uri(sessions.FeedUrl(Name) ?? FeedAddress), session.AuthToken, DeviceId, cancellationToken);
        return connection is null ? (null, null) : (new FeedRead(connection, session.AuthToken), connection);
    }

    private static string Payload(IEnumerable<long>? refs, IEnumerable<string>? names) =>
        new JsonObject { ["instruments"] = new JsonArray((refs ?? []).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()), ["indexes"] = new JsonArray((names ?? []).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) }.ToJsonString(Compact);

    private static string Command(string verb, FeedRead read, string channel, string payload, string? tail = null) => $"{verb} {read.Token} {channel} {payload}{(tail is null ? "" : " " + tail)}";

    private static string WsExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", "NFO" or "CDS" => "NSE", "BFO" => "BSE", _ => exchange };

    private static long? RefId(string? token) => !string.IsNullOrEmpty(token) && token.All(char.IsAsciiDigit) && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>OpenAlgo's _wait_for_feed: look every fifty milliseconds until <paramref name="ready"/>, for at most <paramref name="seconds"/>.</summary>
    private static async Task<bool> WaitAsync(FeedRead read, Func<bool> ready, double seconds, CancellationToken cancellationToken)
    {
        var ticks = (int)Math.Round(seconds / TickSeconds);
        for (var tick = 0; ; tick++)
        {
            if (ready()) return true;
            if (tick >= ticks) return false;
            await read.TickAsync(cancellationToken);
        }
    }

    private static bool BookComplete(BookCache? book) =>
        book is { Ltp: > 0, Oi: not null, Bids: { Length: >= 5 } bids, Asks: { Length: >= 5 } asks } && bids.Take(5).Concat(asks.Take(5)).All(l => l.Price > 0);

    private static JsonObject Zeros() => new() { ["bid"] = 0, ["ask"] = 0, ["open"] = 0, ["high"] = 0, ["low"] = 0, ["ltp"] = 0, ["prev_close"] = 0, ["volume"] = 0, ["oi"] = 0 };

    private static JsonObject QuoteOf(QuoteCache q) => new() { ["bid"] = 0.0, ["ask"] = 0.0, ["open"] = q.Open, ["high"] = q.High, ["low"] = q.Low, ["ltp"] = q.Ltp, ["prev_close"] = q.PrevClose, ["volume"] = q.Volume, ["oi"] = q.VolumeOi };

    private static JsonObject QuoteOf(BookCache b) => new()
    {
        ["bid"] = b.Bids is { Length: > 0 } ? b.Bids[0].Price : 0, ["ask"] = b.Asks is { Length: > 0 } ? b.Asks[0].Price : 0, ["open"] = 0.0, ["high"] = 0.0, ["low"] = 0.0, ["ltp"] = b.Ltp, ["prev_close"] = 0.0, ["volume"] = b.Volume, ["oi"] = b.Oi ?? 0,
    };

    // ---- REST -------------------------------------------------------------------------------------------------

    private static long Whole(JsonNode? node) => (long)Math.Truncate(Float(node));

    /// <summary>
    /// GET /orderbooks/&lt;ref id&gt;?levels=n, the order book of the answer or null when there is none to use: a throttle that never cleared, text that is not JSON, a status that carries no book. An expired session is an error. A refused token is an
    /// error too when <paramref name="refusal"/> says so (OpenAlgo's quote read re-raises it; its depth read does not).
    /// </summary>
    private async Task<JsonObject?> BookOverRestAsync(BrokerSession session, string token, int levels, bool refusal, CancellationToken cancellationToken)
    {
        (int Status, JsonNode? Body, string Text) answer;
        try
        {
            answer = await SendAsync(session, HttpMethod.Get, $"/orderbooks/{token}?levels={levels}", null, false, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return null;
        }

        if (answer.Status == SessionExpired) throw new BrokerException(ExpiredMessage, 401);
        if (answer.Status == 403 && refusal) throw new BrokerException($"{Display} did not accept the session token. Log in to {Display} again.", 401);
        return answer.Body is JsonObject body && body["orderBook"] is JsonObject book && book.Count > 0 ? book : null;
    }

    private async Task<JsonObject?> QuoteOverRestAsync(BrokerSession session, string symbol, string exchange, bool refusal, CancellationToken cancellationToken)
    {
        if (exchange.EndsWith("_INDEX", StringComparison.Ordinal)) return null;
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        if (RefId(record?.Token) is null) return null;
        return await QuoteFromBookAsync(session, record!.Token, refusal, cancellationToken);
    }

    private async Task<JsonObject?> QuoteFromBookAsync(BrokerSession session, string token, bool refusal, CancellationToken cancellationToken)
    {
        if (await BookOverRestAsync(session, token, 1, refusal, cancellationToken) is not { } book) return null;
        var bids = book["bid"] as JsonArray;
        var asks = book["ask"] as JsonArray;
        return new JsonObject
        {
            ["bid"] = bids is { Count: > 0 } ? Float(bids[0]?["p"]) / 100 : 0, ["ask"] = asks is { Count: > 0 } ? Float(asks[0]?["p"]) / 100 : 0, ["open"] = 0, ["high"] = 0, ["low"] = 0, ["ltp"] = Float(book["ltp"]) / 100,
            ["prev_close"] = Float(book["prev_close"]) / 100, ["volume"] = Whole(book["volume"]), ["oi"] = 0,
        };
    }

    // ---- quote ------------------------------------------------------------------------------------------------

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        if (await QuoteOverFeedAsync(session, symbol, exchange, cancellationToken) is { } quote) return quote;
        if (!exchange.EndsWith("_INDEX", StringComparison.Ordinal) && await QuoteOverRestAsync(session, symbol, exchange, true, cancellationToken) is { } rest) return rest;
        return Zeros();
    }

    private async Task<JsonObject?> QuoteOverFeedAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var (read, connection) = await OpenFeedAsync(session, cancellationToken);
        if (read is null) return null;
        await using var _ = connection!;
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        var brokerSymbol = string.IsNullOrEmpty(record?.BrokerSymbol) ? symbol : record.BrokerSymbol;
        var wsExchange = WsExchange(exchange);
        var isIndex = exchange.EndsWith("_INDEX", StringComparison.Ordinal);
        var subscribed = "";
        long? refId = null;
        var bookSubscribed = false;
        try
        {
            bool success;
            if (isIndex)
            {
                success = await read.SendAsync(Command("batch_subscribe", read, "index_bucket", Payload(null, [brokerSymbol]), $"1m {wsExchange}"), cancellationToken);
                if (success) subscribed = "ohlcv";
            }
            else
            {
                success = await read.SendAsync(Command("batch_subscribe", read, "index", Payload(null, [brokerSymbol]), wsExchange), cancellationToken);
                if (success) subscribed = "index";
                if (RefId(record?.Token) is { } id)
                {
                    refId = id;
                    if (await read.SendAsync(Command("batch_subscribe", read, "orderbook", Payload([id], null)), cancellationToken))
                    {
                        await read.SendAsync($"batch_subscribe {read.Token} orderbook_depth 5", cancellationToken);
                        await read.SendAsync(Command("batch_subscribe", read, "greeks", Payload([id], null)), cancellationToken);
                        bookSubscribed = true;
                    }
                }
            }

            if (!success) return null;
            await WaitAsync(read, () => !isIndex && read.Quote(wsExchange, brokerSymbol) is { Ltp: > 0 }, QuoteWaitSeconds, cancellationToken);
            if (read.Quote(wsExchange, brokerSymbol) is { Ltp: > 0 } quote) return QuoteOf(quote);
            if (bookSubscribed && read.Book(refId!.Value) is { Ltp: > 0 } book) return QuoteOf(book);
            return null;
        }
        finally
        {
            if (subscribed == "ohlcv") await read.SendAsync(Command("batch_unsubscribe", read, "index_bucket", Payload(null, [brokerSymbol]), $"1m {wsExchange}"), CancellationToken.None);
            else if (subscribed == "index") await read.SendAsync(Command("batch_unsubscribe", read, "index", Payload(null, [brokerSymbol]), wsExchange), CancellationToken.None);
            if (bookSubscribed)
            {
                await read.SendAsync(Command("batch_unsubscribe", read, "orderbook", Payload([refId!.Value], null)), CancellationToken.None);
                await read.SendAsync(Command("batch_unsubscribe", read, "greeks", Payload([refId.Value], null)), CancellationToken.None);
            }
        }
    }

    // ---- depth ------------------------------------------------------------------------------------------------

    private static JsonObject DepthZeros() => new()
    {
        ["bids"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0, ["quantity"] = 0 }).ToArray()),
        ["asks"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0, ["quantity"] = 0 }).ToArray()),
        ["high"] = 0, ["low"] = 0, ["ltp"] = 0, ["ltq"] = 0, ["open"] = 0, ["prev_close"] = 0, ["volume"] = 0, ["oi"] = 0, ["totalbuyqty"] = 0, ["totalsellqty"] = 0,
    };

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        if (exchange.EndsWith("_INDEX", StringComparison.Ordinal)) return DepthZeros();
        if (await DepthOverFeedAsync(session, symbol, exchange, cancellationToken) is { } feed) return feed;
        if (await DepthOverRestAsync(session, symbol, exchange, cancellationToken) is { } rest) return rest;
        return DepthZeros();
    }

    private static JsonArray Rungs((double Price, long Quantity)[]? levels) =>
        new((levels is null ? Enumerable.Repeat((0.0, 0L), 5) : levels.Take(5)).Select(l => (JsonNode?)new JsonObject { ["price"] = l.Item1, ["quantity"] = l.Item2 }).ToArray());

    private async Task<JsonObject?> DepthOverFeedAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var (read, connection) = await OpenFeedAsync(session, cancellationToken);
        if (read is null) return null;
        await using var _ = connection!;
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        if (RefId(record?.Token) is not { } id) return null;
        if (!await read.SendAsync(Command("batch_subscribe", read, "orderbook", Payload([id], null)), cancellationToken)) return null;
        try
        {
            await read.SendAsync($"batch_subscribe {read.Token} orderbook_depth 5", cancellationToken);
            await read.SendAsync(Command("batch_subscribe", read, "greeks", Payload([id], null)), cancellationToken);
            BookCache? depth = null;
            for (var step = 0; step < DepthSteps; step++)
            {
                if (await WaitAsync(read, () => BookComplete(read.Book(id)), DepthStepSeconds, cancellationToken))
                {
                    depth = read.Book(id);
                    break;
                }

                depth = read.Book(id);
                if (depth is { Ltp: > 0 }) break;
            }

            if (depth is not { Ltp: > 0 }) return null;
            return new JsonObject
            {
                ["bids"] = Rungs(depth.Bids), ["asks"] = Rungs(depth.Asks), ["high"] = 0.0, ["low"] = 0.0, ["ltp"] = depth.Ltp, ["ltq"] = depth.Ltq, ["open"] = 0.0, ["prev_close"] = 0.0, ["volume"] = depth.Volume, ["oi"] = depth.Oi ?? 0,
                ["totalbuyqty"] = depth.TotalBuy, ["totalsellqty"] = depth.TotalSell,
            };
        }
        finally
        {
            await read.SendAsync(Command("batch_unsubscribe", read, "orderbook", Payload([id], null)), CancellationToken.None);
            await read.SendAsync(Command("batch_unsubscribe", read, "greeks", Payload([id], null)), CancellationToken.None);
        }
    }

    private async Task<JsonObject?> DepthOverRestAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        if (RefId(record?.Token) is null) return null;
        if (await BookOverRestAsync(session, record!.Token, 5, false, cancellationToken) is not { } book) return null;
        var bidRows = book["bid"] as JsonArray ?? [];
        var askRows = book["ask"] as JsonArray ?? [];
        JsonArray Side(JsonArray rows) => new(Enumerable.Range(0, 5).Select(i => (JsonNode?)(i < rows.Count ? new JsonObject { ["price"] = Float(rows[i]?["p"]) / 100, ["quantity"] = Whole(rows[i]?["q"]) } : new JsonObject { ["price"] = 0, ["quantity"] = 0 })).ToArray());
        return new JsonObject
        {
            ["bids"] = Side(bidRows), ["asks"] = Side(askRows), ["high"] = Float(book["high"]) / 100, ["low"] = Float(book["low"]) / 100, ["ltp"] = Float(book["ltp"]) / 100, ["ltq"] = Whole(book["ltq"]), ["open"] = Float(book["open"]) / 100,
            ["prev_close"] = Float(book["prev_close"]) / 100, ["volume"] = Whole(book["volume"]), ["oi"] = Whole(book["oi"]), ["totalbuyqty"] = bidRows.Sum(r => Float(r?["q"])), ["totalsellqty"] = askRows.Sum(r => Float(r?["q"])),
        };
    }

    // ---- several quotes ---------------------------------------------------------------------------------------

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var (read, connection) = await OpenFeedAsync(session, cancellationToken);
        if (read is null) return await OneByOneAsync(session, instruments, cancellationToken);
        await using var _ = connection!;
        var books = new List<(SymbolRecord Record, long Id)>();
        var indexes = new List<(SymbolRecord Record, string BrokerSymbol, string WsExchange)>();
        var failed = new List<SymbolRecord>();
        foreach (var record in instruments)
        {
            if (record.Exchange.EndsWith("_INDEX", StringComparison.Ordinal))
                indexes.Add((record, string.IsNullOrEmpty(record.BrokerSymbol) ? record.Symbol : record.BrokerSymbol, record.Exchange == "NSE_INDEX" ? "NSE" : "BSE"));
            else if (RefId(record.Token) is { } id) books.Add((record, id));
            else failed.Add(record);
        }

        var ids = books.Select(b => b.Id).ToList();
        var byExchange = new List<(string WsExchange, List<(SymbolRecord Record, string BrokerSymbol)> Items)>();
        foreach (var item in indexes)
        {
            var group = byExchange.FirstOrDefault(g => g.WsExchange == item.WsExchange);
            if (group.Items is null)
            {
                group = (item.WsExchange, []);
                byExchange.Add(group);
            }

            group.Items.Add((item.Record, item.BrokerSymbol));
        }

        var results = new JsonArray();
        try
        {
            if (ids.Count > 0)
            {
                await read.SendAsync(Command("batch_subscribe", read, "orderbook", Payload(ids, null)), cancellationToken);
                await read.SendAsync($"batch_subscribe {read.Token} orderbook_depth 5", cancellationToken);
                await read.SendAsync(Command("batch_subscribe", read, "greeks", Payload(ids, null)), cancellationToken);
            }

            foreach (var (wsExchange, items) in byExchange)
                await read.SendAsync(Command("batch_subscribe", read, "index_bucket", Payload(null, items.Select(i => i.BrokerSymbol)), $"1m {wsExchange}"), cancellationToken);

            await WaitAsync(read, () => indexes.Count == 0 && books.All(b => BookComplete(read.Book(b.Id))), QuoteWaitSeconds, cancellationToken);

            foreach (var (record, id) in books)
            {
                if (read.Book(id) is { Ltp: > 0 } book) results.Add(Entry(record, QuoteOf(book)));
                else failed.Add(record);
            }

            foreach (var (wsExchange, items) in byExchange)
                foreach (var (record, brokerSymbol) in items)
                {
                    if (read.Quote(wsExchange, brokerSymbol) is { Ltp: > 0 } quote) results.Add(Entry(record, QuoteOf(quote)));
                    else failed.Add(record);
                }
        }
        finally
        {
            if (ids.Count > 0)
            {
                await read.SendAsync(Command("batch_unsubscribe", read, "orderbook", Payload(ids, null)), CancellationToken.None);
                await read.SendAsync(Command("batch_unsubscribe", read, "greeks", Payload(ids, null)), CancellationToken.None);
            }

            foreach (var (wsExchange, items) in byExchange)
                await read.SendAsync(Command("batch_unsubscribe", read, "index_bucket", Payload(null, items.Select(i => i.BrokerSymbol)), $"1m {wsExchange}"), CancellationToken.None);
        }

        foreach (var record in failed)
        {
            JsonObject? rest = null;
            try
            {
                rest = record.Exchange.EndsWith("_INDEX", StringComparison.Ordinal) ? null : await QuoteFromRecordAsync(session, record, cancellationToken);
            }
            catch (BrokerException ex) when (ex.Message != ExpiredMessage)
            {
            }

            results.Add(Entry(record, rest ?? Zeros()));
        }

        return results;
    }

    private static JsonObject Entry(SymbolRecord record, JsonObject data) => new() { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = data };

    private async Task<JsonObject?> QuoteFromRecordAsync(BrokerSession session, SymbolRecord record, CancellationToken cancellationToken) =>
        RefId(record.Token) is null ? null : await QuoteFromBookAsync(session, record.Token, true, cancellationToken);

    /// <summary>With no feed connection each symbol is asked on its own, five at a time, as OpenAlgo's fan-out does: its quote read, which tries the feed again first. A symbol that fails is told as an error beside its name; an expired session fails the lot.</summary>
    private async Task<JsonArray> OneByOneAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(FanOut);
        var tasks = instruments.Select(async record =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = await GetQuoteAsync(session, record.Symbol, record.Exchange, cancellationToken) };
            }
            catch (BrokerException ex) when (ex.Message != ExpiredMessage)
            {
                return new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["error"] = ex.Message };
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return new JsonArray((await Task.WhenAll(tasks)).Select(r => (JsonNode?)r).ToArray());
    }
}
