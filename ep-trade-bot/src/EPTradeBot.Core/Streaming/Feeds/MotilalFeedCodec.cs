using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Motilal Oswal's market data stream (broker/motilal streaming/motilal_adapter.py and api/motilal_websocket.py): a binary login packet carrying the client code, then, once the server has answered, a 10-byte binary register packet for each subscription;
/// 30-byte packets come back (see <see cref="MotilalBook"/>). Every message is raw bytes, so each is hex text here. OpenAlgo republishes every subscription from the socket's store twice a second; EP Trade bot publishes the subscriptions of the scrips a frame
/// touched, as the frame arrives. A subscription is held until the server has answered the login, and every subscription is registered again on each connection.
/// </summary>
public sealed class MotilalFeedCodec : IFeedCodec
{
    private sealed record Sub(string Symbol, string Exchange, string Token, string MotilalExchange, string Segment, int Scrip, int Mode);

    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, Sub> subs = [];
    private readonly List<string> outgoing = [];
    private MotilalBook book = new();
    private string clientCode = "";
    private bool signedIn;

    public string Broker => "motilal";

    public bool MessagesAreHex => true;

    private static string ExchangeType(string brokerExchange) => brokerExchange switch { "NFO" => "NSEFO", "BFO" => "BSEFO", "CDS" => "NSECD", "NCX" => "NCDEX", _ => brokerExchange };

    private static string SegmentOf(string exchange) => exchange is "NFO" or "BFO" or "MCX" or "CDS" or "NCX" ? "DERIVATIVES" : "CASH";

    private static string KeyOf(string symbol, string exchange, int mode) => mode == 3 ? $"{symbol}_{exchange}_{mode}_5" : $"{symbol}_{exchange}_{mode}";

    private static string Register(Sub s, bool add) => Convert.ToHexStringLower(MotilalBook.RegisterPacket(s.MotilalExchange, s.Segment, s.Scrip, add));

    public void Begin(BrokerSession session)
    {
        lock (gate)
        {
            clientCode = session.ClientId ?? "";
            signedIn = false;
        }
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        lock (gate)
        {
            var messages = new List<string>();
            foreach (var r in requests)
            {
                var mode = (int)r.Mode;
                if (mode is < 1 or > 3 || !int.TryParse(r.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scrip)) continue;
                var sub = new Sub(r.Symbol, r.Exchange, r.Token, ExchangeType(r.BrokerExchange), SegmentOf(r.Exchange), scrip, mode);
                subs[KeyOf(r.Symbol, r.Exchange, mode)] = sub;
                book.Register(sub.MotilalExchange, scrip);
                if (signedIn) messages.Add(Register(sub, true));
            }

            return messages;
        }
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var matching = subs.Values.Where(s => s.Symbol == target.Symbol && s.Exchange == target.Exchange && (mode is null || s.Mode == (int)mode)).ToList();
            foreach (var s in matching) subs.Remove(KeyOf(s.Symbol, s.Exchange, s.Mode));
            if (!int.TryParse(target.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scrip)) return [];
            var exchange = ExchangeType(target.BrokerExchange);
            var sub = new Sub(target.Symbol, target.Exchange, target.Token, exchange, SegmentOf(target.Exchange), scrip, mode is { } m ? (int)m : 0);
            book.Unregister(exchange, scrip);
            return signedIn ? [Register(sub, false)] : [];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate)
        {
            signedIn = false;
            outgoing.Clear();
            book = new MotilalBook();
            foreach (var s in subs.Values) book.Register(s.MotilalExchange, s.Scrip);
            return [Convert.ToHexStringLower(MotilalBook.LoginPacket(clientCode))];
        }
    }

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        lock (gate)
        {
            if (!signedIn)
            {
                // The first frame is the server's answer to the login: every subscription is registered now.
                signedIn = true;
                foreach (var s in subs.Values) outgoing.Add(Register(s, true));
            }

            var touched = book.Feed(frame).ToHashSet(StringComparer.Ordinal);
            var ticks = new List<FeedPublish>();
            foreach (var s in subs.Values)
            {
                if (!touched.Contains(MotilalBook.Key(s.MotilalExchange, s.Scrip))) continue;
                if (Tick(s, nowMs) is { } tick) ticks.Add(tick);
            }

            return ticks;
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    public IReadOnlyList<string> TakeOutgoing()
    {
        lock (gate)
        {
            var messages = outgoing.ToList();
            outgoing.Clear();
            return messages;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            subs.Clear();
            outgoing.Clear();
            book = new MotilalBook();
            signedIn = false;
        }
    }

    private static double Of(IReadOnlyDictionary<string, double>? values, string name) => values is not null && values.TryGetValue(name, out var v) ? v : 0;

    private FeedPublish? Tick(Sub s, long nowMs)
    {
        var quote = book.GetQuote(s.MotilalExchange, s.Scrip);
        JsonObject? data = null;
        switch (s.Mode)
        {
            case 1 when quote is { Count: > 0 }:
                data = new JsonObject { ["ltp"] = Of(quote, "ltp"), ["volume"] = Number(Of(quote, "volume")) };
                break;
            case 2 when quote is { Count: > 0 }:
                data = new JsonObject
                {
                    ["ltp"] = Of(quote, "ltp"), ["ltq"] = Number(Of(quote, "ltq")), ["volume"] = Number(Of(quote, "volume")), ["open"] = Of(quote, "open"), ["high"] = Of(quote, "high"), ["low"] = Of(quote, "low"),
                    ["close"] = Of(quote, "prev_close"), ["average_price"] = Of(quote, "avg_trade_price"),
                };
                break;
            case 3:
                var depth = book.GetDepth(s.MotilalExchange, s.Scrip);
                if (quote is not { Count: > 0 } || depth is not { } d || (d.Bids.Count == 0 && d.Asks.Count == 0)) break;
                var oi = book.GetOpenInterest(s.MotilalExchange, s.Scrip);
                data = new JsonObject
                {
                    ["ltp"] = Of(quote, "ltp"), ["volume"] = Number(Of(quote, "volume")), ["open"] = Of(quote, "open"), ["high"] = Of(quote, "high"), ["low"] = Of(quote, "low"), ["close"] = Of(quote, "prev_close"),
                    ["upper_circuit"] = Of(quote, "upper_circuit"), ["lower_circuit"] = Of(quote, "lower_circuit"), ["oi"] = Number(Of(oi, "oi")),
                    ["depth"] = new JsonObject { ["buy"] = Side(d.Bids), ["sell"] = Side(d.Asks) },
                };
                break;
        }

        if (data is null) return null;
        data["symbol"] = s.Symbol;
        data["exchange"] = s.Exchange;
        data["mode"] = s.Mode;
        data["timestamp"] = nowMs;
        var name = s.Mode switch { 1 => "LTP", 2 => "QUOTE", _ => "DEPTH" };
        return new FeedPublish($"{s.Exchange}_{s.Symbol}_{name}", data);
    }

    private static JsonNode Number(double value) => value == Math.Floor(value) && Math.Abs(value) < 1e15 ? JsonValue.Create((long)value) : JsonValue.Create(value);

    private static JsonArray Side(IReadOnlyList<MotilalLevel> levels)
    {
        var rows = new JsonArray();
        for (var i = 0; i < 5; i++)
            rows.Add(i < levels.Count ? new JsonObject { ["price"] = levels[i].Price, ["quantity"] = levels[i].Quantity, ["orders"] = levels[i].Orders } : new JsonObject { ["price"] = 0, ["quantity"] = 0, ["orders"] = 0 });
        return rows;
    }
}
