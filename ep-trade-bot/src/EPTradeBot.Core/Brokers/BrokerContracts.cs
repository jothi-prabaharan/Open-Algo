using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Brokers;

/// <summary>What a broker plugin can do. A capability the plugin lacks is answered with a plain refusal, never a guess.</summary>
[Flags]
public enum BrokerCapability
{
    None = 0,
    Auth = 1 << 0,
    Orders = 1 << 1,
    Portfolio = 1 << 2,
    Funds = 1 << 3,
    Quotes = 1 << 4,
    Depth = 1 << 5,
    History = 1 << 6,
    Gtt = 1 << 7,
    Margin = 1 << 8,
    SymbolMaster = 1 << 9,
    Streaming = 1 << 10,
}

/// <summary>
/// The credentials of one account for one call. Built when the call is made
/// (tokens decrypted, keys read from configuration), never cached in a static,
/// so a token rotated a moment ago is the one used.
/// </summary>
public sealed record BrokerSession(
    Guid AccountId,
    string Broker,
    string AuthToken,
    string? FeedToken,
    string? ClientId,
    IReadOnlyDictionary<string, string> Keys)
{
    public string? Key(string name) => Keys.TryGetValue(name, out var v) ? v : null;
}

/// <summary>An order in OpenAlgo's vocabulary, as it reaches a plugin.</summary>
public sealed record BrokerOrder(
    string Symbol,
    string Exchange,
    string Action,
    int Quantity,
    string PriceType,
    string Product,
    decimal Price,
    decimal TriggerPrice,
    int DisclosedQuantity,
    string Strategy);

/// <summary>A change to a working order, in OpenAlgo's vocabulary.</summary>
public sealed record BrokerOrderChange(
    string OrderId,
    string Symbol,
    string Exchange,
    string Action,
    int Quantity,
    string PriceType,
    string Product,
    decimal Price,
    decimal TriggerPrice,
    int DisclosedQuantity);

/// <summary>
/// The outcome of a write sent to a broker. <see cref="HttpStatus"/> is the status
/// OpenAlgo would answer with (the broker's own status on a refusal).
/// <see cref="MayHaveBeenSent"/> is true when the request left and no answer came
/// back, so the order may exist at the broker.
/// </summary>
public sealed record BrokerCallResult(bool Ok, int HttpStatus, string Message, string? OrderId = null, bool MayHaveBeenSent = false)
{
    public static BrokerCallResult Success(string orderId) => new(true, 200, "", orderId);
    public static BrokerCallResult Refused(int status, string message) => new(false, status, message);
}

/// <summary>A list in OpenAlgo's shape plus its statistics block (order book, holdings).</summary>
public sealed record BrokerBook(JsonArray Rows, JsonObject Statistics);

/// <summary>A broker failed to answer, or answered with an error. The message is safe to show a trader.</summary>
public sealed class BrokerException(string message, int httpStatus = 500, Exception? inner = null) : Exception(message, inner)
{
    public int HttpStatus { get; } = httpStatus;
}

/// <summary>Every broker plugin. Discovered at startup and enabled by the <c>Brokers:Enabled</c> list.</summary>
public interface IBrokerPlugin
{
    /// <summary>Lowercase name, as OpenAlgo's broker directory (zerodha, dhan, ...).</summary>
    string Name { get; }

    string DisplayName { get; }

    BrokerCapability Capabilities { get; }

    /// <summary>Configuration keys this plugin reads when called (for example ApiKey, ApiSecret).</summary>
    IReadOnlyList<string> RequiredKeys { get; }
}

public interface IBrokerOrders : IBrokerPlugin
{
    Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken);

    Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken);

    Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken);
}

public interface IBrokerPortfolio : IBrokerPlugin
{
    /// <summary>Orders in OpenAlgo's shape, before the shared number formatting, with OpenAlgo's statistics.</summary>
    Task<BrokerBook> GetOrderBookAsync(BrokerSession session, CancellationToken cancellationToken);

    Task<JsonArray> GetTradeBookAsync(BrokerSession session, CancellationToken cancellationToken);

    /// <summary>Net positions: symbol, exchange, product, quantity, pnl, average_price, ltp.</summary>
    Task<JsonArray> GetPositionsAsync(BrokerSession session, CancellationToken cancellationToken);

    Task<BrokerBook> GetHoldingsAsync(BrokerSession session, CancellationToken cancellationToken);
}

public interface IBrokerFunds : IBrokerPlugin
{
    Task<JsonObject> GetFundsAsync(BrokerSession session, CancellationToken cancellationToken);
}

public interface IBrokerQuotes : IBrokerPlugin
{
    Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken);

    Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken);
}

/// <summary>Resolves symbols between OpenAlgo's format and a broker's. Backed by that broker's master contract.</summary>
public interface ISymbolMaster
{
    Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken);

    Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken);

    /// <summary>By the broker's instrument token (Dhan's security id) and OpenAlgo exchange.</summary>
    Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken);
}

/// <summary>One master-contract row.</summary>
public sealed record SymbolRecord(
    string Symbol,
    string BrokerSymbol,
    string Exchange,
    string BrokerExchange,
    string Token,
    string? Name,
    string? Expiry,
    double? Strike,
    int LotSize,
    string? InstrumentType,
    double? TickSize);

/// <summary>Finds the plugins that are enabled and offer a capability.</summary>
public interface IBrokerRegistry
{
    IReadOnlyCollection<IBrokerPlugin> Enabled { get; }

    IBrokerPlugin? Find(string broker);

    /// <summary>The plugin for <paramref name="broker"/> when it is enabled and implements <typeparamref name="T"/>.</summary>
    T? Get<T>(string broker) where T : class, IBrokerPlugin;
}
