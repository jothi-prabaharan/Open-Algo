using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Pocketful;

/// <summary>
/// Pocketful's quotes, depth and several quotes (broker/pocketful api/data.py). Nothing is asked for over REST: a feed connection is opened for the read (<see cref="IPocketfulSockets"/>), a subscribe goes out for each instrument (mode marketdata for a quote, full_snapquote for depth) and the
/// packets that come back are read in turn, a quote for ten seconds, depth for fifteen and several quotes for up to half a second an instrument (three to fifteen seconds in all). OpenAlgo's answer is the last packet it read of the mode, which is the one for the instrument unless none came,
/// in which case it is another instrument's (or nothing); that is kept. Prices are in paise.
/// </summary>
public sealed partial class PocketfulPlugin : IBrokerMultiQuotes
{
    private const string FeedAddress = "wss://trade.pocketful.in";
    private const int BatchSize = 50;

    private static int ExchangeCode(string exchange) => exchange switch { "NSE" or "NSE_INDEX" => 1, "NFO" => 2, "CDS" => 3, "MCX" => 4, "BSE" or "BSE_INDEX" => 6, "BFO" => 7, _ => 1 };

    private Uri FeedUri(string clientId, string token) => new($"{sessions.FeedUrl(Name) ?? FeedAddress}/ws/v1/feeds?login_id={clientId}&access_token={token}");

    private static string Frame(string action, string mode, IEnumerable<(int Code, long Token)> instruments) =>
        new JsonObject { ["a"] = action, ["v"] = new JsonArray(instruments.Select(i => (JsonNode?)new JsonArray(i.Code, i.Token)).ToArray()), ["m"] = mode }.ToJsonString();

    private async Task<(int Code, long Token)> InstrumentAsync(string symbol, string exchange, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);
        return long.TryParse(info.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token) ? (ExchangeCode(exchange), token) : throw new BrokerException($"{symbol} on {exchange} has no token {Display} could ask for. Download the master contract again.", 400);
    }

    /// <summary>Python's <c>str(x)</c> of a packet value: a whole number as its digits, anything else as <see cref="JsonRead.Str"/> reads it.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : Str(node);

    /// <summary>Python's <c>_is_packet_for</c>: the packet names this instrument, as text.</summary>
    private static bool IsPacketFor(JsonObject packet, long token)
    {
        if (packet.Count == 0) return false;
        var named = Truthy(packet["instrument_token"]) ? packet["instrument_token"] : packet["instrumentToken"];
        return Truthy(named) && Text(named) == token.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The mode of a frame as OpenAlgo's socket reads it, or null when it cannot be told.</summary>
    private static int? ModeOf(PocketfulFrame frame)
    {
        if (frame.Binary) return PocketfulPackets.ModeOf(frame.Bytes);
        return PocketfulPackets.ModeOfText(frame.Text) is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.TryGetValue<double>(out var d) && d == Math.Floor(d) ? (int)d : null;
    }

    private static double Price(JsonObject packet, string key) => Truthy(packet[key]) ? Float(packet[key]) / 100 : 0;

    private static JsonNode? Plain(JsonObject packet, string key, JsonNode? fallback) => packet.TryGetPropertyValue(key, out var v) ? v?.DeepClone() : fallback;

    // ---- quote -----------------------------------------------------------------------------------------------

    private async Task<(JsonObject Packet, bool Connected)> ReadDetailedAsync(BrokerSession session, IReadOnlyList<(int Code, long Token)> instruments, Func<JsonObject, bool> done, TimeSpan wait, CancellationToken cancellationToken)
    {
        var clientId = await ClientIdAsync(session, cancellationToken);
        var latest = new JsonObject();
        // The broker is told only when the last holder of an instrument lets go, as OpenAlgo's subscriber count does.
        var holders = new Dictionary<(int, long), int>();
        foreach (var i in instruments) holders[i] = holders.GetValueOrDefault(i) + 1;
        var unsubscribes = new List<string>();
        foreach (var i in instruments)
        {
            if (holders[i] <= 1) unsubscribes.Add(Frame("unsubscribe", "marketdata", [i]));
            else holders[i]--;
        }

        var connected = await Sockets.RunAsync(FeedUri(clientId, session.AuthToken), instruments.Select(i => Frame("subscribe", "marketdata", [i])).ToList(), unsubscribes, frame =>
        {
            if (ModeOf(frame) != PocketfulPackets.Detailed) return false;
            latest = frame.Binary ? PocketfulPackets.DecodeDetailed(frame.Bytes) : [];
            return done(latest);
        }, wait, cancellationToken);
        return (latest, connected);
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var instrument = await InstrumentAsync(symbol, exchange, cancellationToken);
        var (packet, connected) = await ReadDetailedAsync(session, [instrument], p => IsPacketFor(p, instrument.Token), TimeSpan.FromSeconds(10), cancellationToken);
        if (!connected) throw new BrokerException($"{Display} did not open its market data connection, so there is no quote for {symbol} on {exchange}. Try again in a moment.", 502);
        if (packet.Count == 0) throw new BrokerException($"{Display} did not give a quote for {symbol} on {exchange}. Try again in a moment.", 502);
        var close = Price(packet, "close_price");
        var ltp = Price(packet, "last_traded_price");
        return new JsonObject
        {
            ["ask"] = Price(packet, "best_ask_price"), ["bid"] = Price(packet, "best_bid_price"), ["high"] = Price(packet, "high_price"), ["low"] = Price(packet, "low_price"), ["ltp"] = ltp, ["open"] = Price(packet, "open_price"),
            ["prev_close"] = close, ["volume"] = Plain(packet, "trade_volume", 0), ["oi"] = Plain(packet, "currentOpenInterest", 0), ["change"] = close != 0 ? ltp - close : 0,
        };
    }

    // ---- depth -----------------------------------------------------------------------------------------------

    private static JsonArray Ladder(JsonObject packet, string prices, string quantities, string orders)
    {
        var rows = new JsonArray();
        var p = packet[prices] as JsonArray ?? [];
        var q = packet[quantities] as JsonArray ?? [];
        var o = packet[orders] as JsonArray ?? [];
        for (var i = 0; i < Math.Min(5, p.Count); i++)
            rows.Add(new JsonObject { ["price"] = Truthy(p[i]) ? Float(p[i]) / 100 : 0, ["quantity"] = i < q.Count ? q[i]?.DeepClone() : 0, ["orders"] = i < o.Count ? o[i]?.DeepClone() : 0 });
        while (rows.Count < 5) rows.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0, ["orders"] = 0 });
        return rows;
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var instrument = await InstrumentAsync(symbol, exchange, cancellationToken);
        var clientId = await ClientIdAsync(session, cancellationToken);
        var latest = new JsonObject();
        var connected = await Sockets.RunAsync(FeedUri(clientId, session.AuthToken), [Frame("subscribe", "full_snapquote", [instrument])], [Frame("unsubscribe", "full_snapquote", [instrument])], frame =>
        {
            if (ModeOf(frame) != PocketfulPackets.Snapquote) return false;
            latest = frame.Binary ? PocketfulPackets.DecodeSnapquote(frame.Bytes) : PocketfulPackets.DecodeSnapquote(frame.Text);
            return IsPacketFor(latest, instrument.Token);
        }, TimeSpan.FromSeconds(15), cancellationToken);
        if (!connected) throw new BrokerException($"{Display} did not open its market data connection, so there is no depth for {symbol} on {exchange}. Try again in a moment.", 502);
        if (!latest.ContainsKey("instrument_token")) throw new BrokerException($"{Display} did not give the market depth for {symbol} on {exchange}. Try again in a moment.", 502);
        return new JsonObject
        {
            ["asks"] = Ladder(latest, "askPrices", "askQtys", "sellers"), ["bids"] = Ladder(latest, "bidPrices", "bidQtys", "buyers"), ["high"] = Price(latest, "high"), ["low"] = Price(latest, "low"), ["ltp"] = Price(latest, "averageTradePrice"),
            ["ltq"] = 0, ["oi"] = 0, ["open"] = Price(latest, "open"), ["prev_close"] = Price(latest, "close"), ["totalbuyqty"] = Plain(latest, "totalBuyQty", 0), ["totalsellqty"] = Plain(latest, "totalSellQty", 0), ["volume"] = Plain(latest, "volume", 0),
        };
    }

    // ---- several quotes --------------------------------------------------------------------------------------

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var start = 0; start < instruments.Count; start += BatchSize)
            foreach (var item in await BatchAsync(session, instruments.Skip(start).Take(BatchSize).ToList(), cancellationToken)) results.Add(item);
        return results;
    }

    private async Task<List<JsonObject>> BatchAsync(BrokerSession session, IReadOnlyList<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var skipped = new List<JsonObject>();
        var asked = new List<(int Code, long Token)>();
        var byToken = new OrderedDictionary<long, SymbolRecord>();
        foreach (var record in batch)
        {
            if (!long.TryParse(record.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token))
            {
                skipped.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["error"] = $"{record.Symbol} on {record.Exchange} has no token {Display} could ask for." });
                continue;
            }

            asked.Add((ExchangeCode(record.Exchange), token));
            byToken[token] = record;
        }

        if (asked.Count == 0) return skipped;
        var received = new Dictionary<long, JsonObject>();
        var wait = TimeSpan.FromSeconds(Math.Min(Math.Max(asked.Count * 0.5, 3), 15));
        var (_, connected) = await ReadDetailedAsync(session, asked, packet =>
        {
            if (packet.Count > 0)
            {
                var named = Truthy(packet["instrument_token"]) ? packet["instrument_token"] : packet["instrumentToken"];
                if (Truthy(named) && long.TryParse(Text(named), NumberStyles.Integer, CultureInfo.InvariantCulture, out var token) && byToken.ContainsKey(token)) received[token] = packet;
            }

            return received.Count >= asked.Count;
        }, wait, cancellationToken);
        var results = new List<JsonObject>(skipped);
        if (!connected)
        {
            foreach (var record in batch) results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["error"] = $"{Display} did not open its market data connection. Try again in a moment." });
            return results;
        }

        foreach (var (token, record) in byToken)
        {
            if (!received.TryGetValue(token, out var p))
            {
                results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["error"] = "No data received" });
                continue;
            }

            results.Add(new JsonObject
            {
                ["symbol"] = record.Symbol, ["exchange"] = record.Exchange,
                ["data"] = new JsonObject
                {
                    ["bid"] = Price(p, "best_bid_price"), ["ask"] = Price(p, "best_ask_price"), ["open"] = Price(p, "open_price"), ["high"] = Price(p, "high_price"), ["low"] = Price(p, "low_price"), ["ltp"] = Price(p, "last_traded_price"),
                    ["prev_close"] = Price(p, "close_price"), ["volume"] = Plain(p, "trade_volume", 0), ["oi"] = Plain(p, "currentOpenInterest", 0),
                },
            });
        }

        return results;
    }
}
