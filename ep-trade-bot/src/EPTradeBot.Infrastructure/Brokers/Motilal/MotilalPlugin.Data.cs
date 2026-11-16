using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

/// <summary>
/// Motilal quotes and history (broker/motilal api/data.py). A quote is one REST call (getltpdata, in paisa; getindexltpdata for an index, in rupees). A dealer login is told by an error (MO1062) and sends the client code from then on.
/// Motilal publishes no history endpoint: the one candle served is today's daily bar, built from the live quote.
/// </summary>
public sealed partial class MotilalPlugin
{
    private static readonly HashSet<string> IndexExchanges = ["NSE_INDEX", "BSE_INDEX", "MCX_INDEX"];

    /// <summary>Whether this server has been told a login is a dealer's (it then sends the client code with every report call). Like Python's, it is learned once and kept.</summary>
    private volatile bool dealerMode;

    /// <summary>Which spelling of the index exchange field the broker accepted, once learned.</summary>
    private volatile string? indexExchangeField;

    private async Task<Reply> ReportAsync(BrokerSession session, string endpoint, JsonObject payload, CancellationToken cancellationToken)
    {
        var body = (JsonObject)payload.DeepClone();
        if (dealerMode && !string.IsNullOrEmpty(session.ClientId)) body["clientcode"] = session.ClientId;
        var reply = await CallAsync(session, endpoint, body, cancellationToken);
        if (reply.Ok || dealerMode || reply.Body is null) return reply;
        var code = (Str(reply.Body["errorcode"]) ?? "").Trim().ToUpperInvariant();
        var message = (Str(reply.Body["message"]) ?? "").ToLowerInvariant();
        if (code != "MO1062" && !(message.Contains("client code", StringComparison.Ordinal) && message.Contains("provide", StringComparison.Ordinal))) return reply;
        dealerMode = true;
        if (string.IsNullOrEmpty(session.ClientId)) return reply;
        body["clientcode"] = session.ClientId;
        return await CallAsync(session, endpoint, body, cancellationToken);
    }

    private async Task<SymbolRecord> InstrumentAsync(string symbol, string exchange, CancellationToken cancellationToken) =>
        await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);

    private static double Paisa(JsonNode? node) => Truthy(node) ? Float(node) / 100.0 : 0.0;

    private static double Plain(JsonNode? node) => Truthy(node) ? Float(node) : 0.0;

    private BrokerException Unavailable(string what, Reply reply) =>
        new($"{Display} did not give {what}: {reply.Failure ?? "no reason was given."} Try again in a moment.", reply.Status is >= 400 and < 600 ? reply.Status : 502);

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        if (IndexExchanges.Contains(exchange)) return await IndexQuoteAsync(session, symbol, exchange, cancellationToken);
        var info = await InstrumentAsync(symbol, exchange, cancellationToken);
        var token = long.TryParse(info.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : throw new BrokerException($"{symbol} on {exchange} has no token in the {Display} master contract. Download the master contract again.", 400);
        var reply = await ReportAsync(session, "getltpdata", new JsonObject { ["exchange"] = MotilalExchange(exchange), ["scripcode"] = token }, cancellationToken);
        if (!Succeeded(reply)) throw Unavailable($"the quote for {symbol}", reply);
        if (!Truthy(reply.Body!["data"]) || reply.Body["data"] is not JsonObject data) throw new BrokerException($"{Display} sent no quote for {symbol}. Try again in a moment.", 502);
        return new JsonObject
        {
            ["bid"] = Paisa(data["bid"]), ["ask"] = Paisa(data["ask"]), ["open"] = Paisa(data["open"]), ["high"] = Paisa(data["high"]), ["low"] = Paisa(data["low"]), ["ltp"] = Paisa(data["ltp"]),
            ["prev_close"] = Paisa(data["close"]), ["volume"] = Int(data["volume"]), ["oi"] = 0,
        };
    }

    private async Task<JsonObject> IndexQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var motilal = exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", _ => null };
        if (motilal is null) throw new BrokerException($"{Display} gives no quote for {symbol}: it can quote NSE and BSE indices only.", 400);
        var info = await InstrumentAsync(symbol, exchange, cancellationToken);
        Reply reply = new(0, null, null);
        string[] candidates = indexExchangeField is { } learned ? [learned] : ["exchangename", "exchange"];
        foreach (var field in candidates)
        {
            reply = await ReportAsync(session, "getindexltpdata", new JsonObject { [field] = motilal, ["scripcode"] = info.Token }, cancellationToken);
            if (Succeeded(reply))
            {
                indexExchangeField = field;
                break;
            }

            if ((Str(reply.Body?["errorcode"]) ?? "").Trim().ToUpperInvariant() != "MO1051") break;
        }

        if (!Succeeded(reply)) throw Unavailable($"the quote for {symbol}", reply);
        var rows = reply.Body!["data"] switch { JsonArray list => list.OfType<JsonObject>().ToList(), JsonObject one => [one], _ => [] };
        if (rows.Count == 0) throw new BrokerException($"{Display} sent no quote for {symbol}. Try again in a moment.", 502);
        var row = rows.FirstOrDefault(r => (Str(r["scripcode"]) ?? "") == info.Token) ?? rows[0];
        return new JsonObject
        {
            ["bid"] = 0.0, ["ask"] = 0.0, ["open"] = Plain(row["open"]), ["high"] = Plain(row["high"]), ["low"] = Plain(row["low"]), ["ltp"] = Plain(row["ltp"]), ["prev_close"] = Plain(row["close"]), ["volume"] = 0, ["oi"] = 0,
        };
    }

    /// <summary>The last price of an instrument, for a market order's protection. A failed read is an error, and the caller sends the order as it was asked.</summary>
    private async Task<double> LastPriceAsync(BrokerSession session, SymbolRecord info, CancellationToken cancellationToken) =>
        Float((await GetQuoteAsync(session, info.Symbol, info.Exchange, cancellationToken))["ltp"]);

    public Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken) =>
        throw new BrokerException($"{Display} market depth is not available yet.", 501);

    // ---- history ---------------------------------------------------------------------------------------------

    private static readonly HashSet<string> DailyIntervals = ["D", "1D", "DAY", "DAILY"];

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["D"];

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!DailyIntervals.Contains((interval ?? "").Trim().ToUpperInvariant()))
            throw new BrokerException($"Interval '{interval}' is not available from {Display}: it gives no history, only today's daily candle (interval D).", 400);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).DateTime);
        if (!(start <= today && today <= end)) return [];
        var quote = await GetQuoteAsync(session, instrument.Symbol, instrument.Exchange, cancellationToken);
        var close = Float(quote["ltp"]);
        var open = Float(quote["open"]);
        var high = Float(quote["high"]);
        var low = Float(quote["low"]);
        if (close <= 0 && open <= 0) return [];
        high = Math.Max(high, Math.Max(open, close));
        var positive = new[] { low, open, close }.Where(x => x > 0).ToList();
        low = positive.Count > 0 ? positive.Min() : 0.0;
        var stamp = new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        return new JsonArray(new JsonObject
        {
            ["timestamp"] = stamp, ["open"] = open != 0 ? open : close, ["high"] = high, ["low"] = low != 0 ? low : close, ["close"] = close, ["volume"] = Int(quote["volume"]), ["oi"] = Int(quote["oi"]),
        });
    }
}
