using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Kotak;

/// <summary>
/// Kotak market data (broker/kotak/api/data.py): GET /script-details/1.0/quotes/neosymbol/{exchange|token,...}/all, with the access token as the Authorization header. A request that gets 429 is tried again after half a second,
/// a second and two (a Retry-After is honoured). At most 25 instruments go in one request, 0.2 s apart.
/// </summary>
public sealed partial class KotakPlugin
{
    private const int QuoteBatch = 25;

    /// <summary>Python's quote(text, safe="|,"): letters, digits and _ . - ~ | , stay; every other byte is its percent form.</summary>
    private static string QuoteQuery(string text)
    {
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' or '|' or ',')) result.Append(c);
            else result.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private async Task<JsonArray?> QuotesAsync(Neo neo, string query, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{neo.Base}/script-details/1.0/quotes/neosymbol/{QuoteQuery(query)}/all");
            request.Headers.TryAddWithoutValidation("Authorization", neo.Access);
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return null;
            }

            using (response)
            {
                if ((int)response.StatusCode == 429 && attempt < 3)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt));
                    await Task.Delay(sessions.RetryBackoff(Name, wait), cancellationToken);
                    continue;
                }

                return (int)response.StatusCode == 200 ? await ReadAsync(response, cancellationToken) as JsonArray : null;
            }
        }
    }

    /// <summary>The last price of each (symbol, exchange) the positions hold, for those Kotak answers. A read that fails leaves its instruments without one: a price is a convenience and never fails the positions.</summary>
    private async Task<Dictionary<(string Symbol, string Exchange), double>> LtpsAsync(Neo neo, List<JsonObject> rows, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<(string, string), double>();
        var wanted = new List<(string Symbol, string Exchange)>();
        foreach (var r in rows)
        {
            var exchange = Exchange(Str(r["exSeg"]));
            var symbol = (await SymbolOrNullAsync(r, exchange, cancellationToken)) ?? Str(r["trdSym"]) ?? "";
            if (symbol.Length > 0 && !wanted.Contains((symbol, exchange))) wanted.Add((symbol, exchange));
        }

        for (var i = 0; i < wanted.Count; i += QuoteBatch)
        {
            var queries = new List<(string Query, (string Symbol, string Exchange) Item)>();
            foreach (var item in wanted.Skip(i).Take(QuoteBatch))
            {
                var record = await symbols.FindAsync(Name, item.Symbol, item.Exchange, cancellationToken);
                if (record is null || string.IsNullOrEmpty(record.Token) || string.IsNullOrEmpty(record.BrokerExchange)) continue;
                var segment = Segment(record.BrokerExchange) is { } mapped ? Str(mapped)! : record.BrokerExchange;
                queries.Add(($"{segment}|{record.Token}", item));
            }

            if (queries.Count > 0 && await QuotesAsync(neo, string.Join(',', queries.Select(q => q.Query)), cancellationToken) is { } answer)
            {
                var lookup = new Dictionary<string, JsonObject>();
                foreach (var quote in answer.OfType<JsonObject>())
                {
                    var exchange = Str(quote["exchange"]) ?? "";
                    lookup[$"{exchange}|{Str(quote["exchange_token"]) ?? ""}"] = quote;
                    if (Truthy(quote["display_symbol"])) lookup[$"{exchange}|{(Str(quote["display_symbol"]) ?? "").Replace("-EQ", "", StringComparison.Ordinal).Replace("-IN", "", StringComparison.Ordinal)}"] = quote;
                }

                foreach (var (query, item) in queries)
                {
                    var quote = lookup.GetValueOrDefault(query) ?? lookup.FirstOrDefault(kv => string.Equals(kv.Key, query, StringComparison.OrdinalIgnoreCase)).Value;
                    if (quote is not null && Float(quote["ltp"]) is var ltp && ltp != 0) prices[item] = ltp;
                }
            }

            if (i + QuoteBatch < wanted.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromMilliseconds(200)), cancellationToken);
        }

        return prices;
    }
}
