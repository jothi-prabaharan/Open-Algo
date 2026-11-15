using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Definedge;

/// <summary>
/// Definedge market data (broker/definedge api/data.py): GET /quotes/{exchange}/{token} on the trading host, an index asked for under its cash exchange, twenty to a batch. A derivative's open interest is the seventh column
/// of the last candle of its one-minute history on the data host, from nine fifteen four days back to now, kept for a minute (and kept as zero when the read fails). The previous close is the day's open, as OpenAlgo has it.
/// </summary>
public sealed partial class DefinedgePlugin
{
    private const string DataUrl = "https://data.definedgesecurities.com/sds";
    private const int QuoteBatch = 20;
    private static readonly HashSet<string> DerivativeExchanges = ["NFO", "BFO", "MCX", "CDS", "BCD"];
    private static readonly ConcurrentDictionary<(string, string), (long Oi, DateTimeOffset Expires)> OiCache = new();

    private static string ApiExchange(string exchange) => exchange switch { "NSE_INDEX" => "NSE", "BSE_INDEX" => "BSE", "MCX_INDEX" => "MCX", _ => exchange };

    /// <summary>Python's float(x) of a quote field: a number, or text that reads as one. Absent is the default; null and text that is not a number cannot be read.</summary>
    private static double Strict(JsonObject quote, string name, double fallback = 0)
    {
        if (!quote.TryGetPropertyValue(name, out var node)) return fallback;
        if (BookFormatting.Number(node) is { } n) return n;
        if (Str(node) is { } text && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new FormatException("Not a number.");
    }

    /// <summary>int(x or 0): absent, null, empty and zero are zero; a whole number or its text is that number; anything else cannot be read.</summary>
    private static long StrictInt(JsonObject quote, string name)
    {
        if (!quote.TryGetPropertyValue(name, out var node) || !Truthy(node)) return 0;
        if (BookFormatting.Number(node) is { } n) return (long)Math.Truncate(n);
        if (Str(node) is { } text && long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        throw new FormatException("Not a whole number.");
    }

    private async Task<JsonObject> QuoteResponseAsync(BrokerSession session, string symbol, string exchange, string what, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken)
            ?? throw new BrokerException($"{symbol} on {exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.", 400);
        var url = $"{sessions.BaseUrl(Name, DefaultBaseUrl)}/quotes/{ApiExchange(exchange)}/{record.Token}";
        var (status, body, _) = await SendRawAsync(session, url, cancellationToken);
        if (status != 200 || body is not JsonObject quote || Str(quote["status"]) != "SUCCESS") throw new BrokerException($"{Display} could not give the {what}. Try again in a moment.", 500);
        return quote;
    }

    private async Task<long> OpenInterestAsync(BrokerSession session, string segment, string token, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (OiCache.TryGetValue((segment, token), out var cached) && cached.Expires > now) return cached.Oi;
        long oi = 0;
        try
        {
            var local = now.ToOffset(TimeSpan.FromHours(5.5));
            var url = $"{DataUrl}/history/{segment}/{token}/minute/{local.AddDays(-4):ddMMyyyy}0915/{local:ddMMyyyyHHmm}";
            var (status, _, text) = await SendRawAsync(session, url, cancellationToken);
            if (status == 200 && text.Trim() is { Length: > 0 } trimmed)
            {
                var last = trimmed[(trimmed.LastIndexOf('\n') + 1)..].Split(',');
                if (last.Length >= 7 && double.TryParse(last[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) oi = (long)Math.Truncate(d);
            }
        }
        catch (BrokerException)
        {
            oi = 0;
        }

        OiCache[(segment, token)] = (oi, now.AddSeconds(60));
        return oi;
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var quote = await QuoteResponseAsync(session, symbol, exchange, "quote", cancellationToken);
        try
        {
            var open = Strict(quote, "day_open", Strict(quote, "ltp"));
            long oi = 0;
            if (DerivativeExchanges.Contains(exchange) && (await symbols.FindAsync(Name, symbol, exchange, cancellationToken))?.Token is { Length: > 0 } token) oi = await OpenInterestAsync(session, exchange, token, cancellationToken);
            return new JsonObject
            {
                ["bid"] = Strict(quote, "best_bid_price1"), ["ask"] = Strict(quote, "best_ask_price1"), ["open"] = Strict(quote, "day_open"), ["high"] = Strict(quote, "day_high"), ["low"] = Strict(quote, "day_low"),
                ["ltp"] = Strict(quote, "ltp"), ["prev_close"] = open, ["volume"] = StrictInt(quote, "volume"), ["oi"] = oi,
            };
        }
        catch (FormatException)
        {
            throw new BrokerException($"{Display} sent a quote that could not be read. Try again in a moment.", 500);
        }
    }

    private static JsonObject Level(JsonObject quote, string price, string quantity) =>
        new() { ["price"] = Truthy(quote[price]) ? Strict(quote, price) : 0, ["quantity"] = StrictInt(quote, quantity) };

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var quote = await QuoteResponseAsync(session, symbol, exchange, "depth", cancellationToken);
        try
        {
            var bids = new JsonArray();
            var asks = new JsonArray();
            for (var i = 1; i <= 5; i++)
            {
                bids.Add(Level(quote, $"best_bid_price{i}", $"best_bid_qty{i}"));
                asks.Add(Level(quote, $"best_ask_price{i}", $"best_ask_qty{i}"));
            }

            long oi = 0;
            if (DerivativeExchanges.Contains(exchange) && (await symbols.FindAsync(Name, symbol, exchange, cancellationToken))?.Token is { Length: > 0 } token) oi = await OpenInterestAsync(session, exchange, token, cancellationToken);
            return new JsonObject
            {
                ["bids"] = bids, ["asks"] = asks, ["high"] = Strict(quote, "day_high"), ["low"] = Strict(quote, "day_low"), ["ltp"] = Strict(quote, "ltp"), ["ltq"] = StrictInt(quote, "last_traded_qty"), ["open"] = Strict(quote, "day_open"),
                ["prev_close"] = Strict(quote, "day_open"), ["volume"] = StrictInt(quote, "volume"), ["oi"] = oi, ["totalbuyqty"] = bids.Sum(b => b!["quantity"]!.GetValue<long>()), ["totalsellqty"] = asks.Sum(a => a!["quantity"]!.GetValue<long>()),
            };
        }
        catch (FormatException)
        {
            throw new BrokerException($"{Display} sent a quote that could not be read. Try again in a moment.", 500);
        }
    }

    private async Task<JsonObject> OneAsync(BrokerSession session, SymbolRecord record, CancellationToken cancellationToken)
    {
        var entry = new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange };
        try
        {
            var (status, body, _) = await SendRawAsync(session, $"{sessions.BaseUrl(Name, DefaultBaseUrl)}/quotes/{ApiExchange(record.Exchange)}/{record.Token}", cancellationToken);
            if (status != 200) return Failed(entry, $"{Display} answered {status} for this quote.");
            if (body is not JsonObject quote || Str(quote["status"]) != "SUCCESS") return Failed(entry, $"{Display} could not give this quote.");
            entry["data"] = new JsonObject
            {
                ["bid"] = Strict(quote, "best_bid_price1"), ["ask"] = Strict(quote, "best_ask_price1"), ["open"] = Strict(quote, "day_open"), ["high"] = Strict(quote, "day_high"), ["low"] = Strict(quote, "day_low"),
                ["ltp"] = Strict(quote, "ltp"), ["prev_close"] = Strict(quote, "day_open", Strict(quote, "ltp")), ["volume"] = Truthy(quote["volume"]) || quote.ContainsKey("volume") ? (long)Strict(quote, "volume") : 0, ["oi"] = 0,
            };
            return entry;
        }
        catch (Exception ex) when (ex is FormatException or BrokerException)
        {
            return Failed(entry, $"{Display} could not give this quote.");
        }
    }

    private static JsonObject Failed(JsonObject entry, string message)
    {
        entry["error"] = message;
        return entry;
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        foreach (var batch in instruments.Chunk(QuoteBatch))
        {
            var entries = new List<JsonObject>();
            foreach (var record in batch) entries.Add(await OneAsync(session, record, cancellationToken));
            foreach (var entry in entries)
            {
                if (entry["data"] is JsonObject data && DerivativeExchanges.Contains(Str(entry["exchange"]) ?? "")
                    && batch.FirstOrDefault(r => r.Symbol == Str(entry["symbol"])) is { } record) data["oi"] = await OpenInterestAsync(session, ApiExchange(record.Exchange), record.Token, cancellationToken);
                results.Add(entry);
            }
        }

        return results;
    }
}
