using System.Collections.Concurrent;
using EPTradeBot.Core.Trading;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>In-memory latest-quote store. Lock-free reads and writes of whole immutable records.</summary>
public sealed class QuoteBook : IQuoteSource
{
    private readonly ConcurrentDictionary<(string Exchange, string Symbol), Quote> _quotes = new();

    public Quote? Get(string exchange, string symbol) => _quotes.TryGetValue((exchange, symbol), out var q) ? q : null;

    public void Set(string exchange, string symbol, Quote quote) => _quotes[(exchange, symbol)] = quote;
}
