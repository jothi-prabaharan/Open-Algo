using EPTradeBot.Core.Entities.Audit;

namespace EPTradeBot.Core.Trading;

/// <summary>
/// One order somebody or something wants placed, before any check has run. Every
/// entry point (manual, webhook, strategy, Flow, agent, Action Center, kill switch)
/// builds one of these and hands it to the single order dispatcher.
/// </summary>
public sealed record OrderIntent
{
    public required Guid AccountId { get; init; }
    public required OrderSource Source { get; init; }
    public required string Symbol { get; init; }
    public required string Exchange { get; init; }

    /// <summary>BUY or SELL.</summary>
    public required string Action { get; init; }

    public required int Quantity { get; init; }

    /// <summary>MIS, NRML or CNC.</summary>
    public string Product { get; init; } = "MIS";

    /// <summary>MARKET, LIMIT, SL or SL-M.</summary>
    public string PriceType { get; init; } = "MARKET";

    public decimal Price { get; init; }
    public decimal TriggerPrice { get; init; }
    public string Strategy { get; init; } = "";
    public string Actor { get; init; } = "";
    public string? CorrelationId { get; init; }

    public bool IsBuy => string.Equals(Action, "BUY", StringComparison.OrdinalIgnoreCase);

    /// <summary>+1 for BUY, -1 for SELL.</summary>
    public int Sign => IsBuy ? 1 : -1;
}

/// <summary>A check that refused an order, worded for a trader.</summary>
public sealed record Refusal(string Code, string Message);

/// <summary>Best bid, best ask and last traded price. Any of them may be missing.</summary>
public sealed record Quote(decimal? Ltp, decimal? Bid = null, decimal? Ask = null);

public enum DispatchStatus
{
    /// <summary>Accepted by the destination (filled or open).</summary>
    Accepted,

    /// <summary>A guard, risk or validation check refused it; nothing was sent.</summary>
    Refused,

    /// <summary>Held in the Action Center for approval.</summary>
    Queued,

    /// <summary>The destination failed to take it.</summary>
    Failed,
}

public sealed record DispatchResult(DispatchStatus Status, string Message, string? OrderId = null, string? Code = null)
{
    public static DispatchResult Refuse(Refusal r) => new(DispatchStatus.Refused, r.Message, Code: r.Code);
}
