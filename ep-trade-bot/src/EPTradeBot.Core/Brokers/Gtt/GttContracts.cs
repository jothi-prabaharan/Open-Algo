using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Brokers.Gtt;

/// <summary>
/// A GTT (good till triggered) order in OpenAlgo's vocabulary, after the request rules have run.
/// SINGLE carries one trigger (<see cref="Trigger"/>) and one limit (<see cref="Price"/>); OCO carries a stoploss leg and a target leg.
/// </summary>
public sealed record GttOrder(
    SymbolRecord Instrument,
    string TriggerType,
    string Action,
    string Product,
    int Quantity,
    string PriceType,
    decimal Price,
    decimal TriggerSl,
    decimal TriggerTg,
    decimal StopLoss,
    decimal Target,
    string? TriggerId = null,
    string? Strategy = null)
{
    public bool IsOco => TriggerType == "OCO";

    /// <summary>The one trigger of a SINGLE GTT: the stoploss trigger if given, else the target trigger.</summary>
    public decimal Trigger => TriggerSl > 0 ? TriggerSl : TriggerTg;
}

/// <summary>The outcome of a GTT write. <see cref="HttpStatus"/> is the status OpenAlgo answers with on a refusal.</summary>
public sealed record GttResult(bool Ok, int HttpStatus, string Message, string? TriggerId = null)
{
    public static GttResult Success(string triggerId) => new(true, 200, "", triggerId);
    public static GttResult Refused(int status, string message) => new(false, status, message);
}

/// <summary>GTT triggers at a broker: place, modify (a full replacement), cancel and the book.</summary>
public interface IBrokerGtt : IBrokerPlugin
{
    Task<GttResult> PlaceGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken);

    Task<GttResult> ModifyGttAsync(BrokerSession session, GttOrder order, CancellationToken cancellationToken);

    Task<GttResult> CancelGttAsync(BrokerSession session, string triggerId, CancellationToken cancellationToken);

    /// <summary>The book in OpenAlgo's shape: trigger_id, trigger_type, status, symbol, exchange, trigger_prices, last_price, legs, created_at, updated_at, expires_at.</summary>
    Task<JsonArray> GetGttBookAsync(BrokerSession session, bool includeHistory, CancellationToken cancellationToken);
}
