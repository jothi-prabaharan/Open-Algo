namespace EPTradeBot.Core.Entities.Audit;

/// <summary>Where an order request came from.</summary>
public enum OrderSource
{
    Manual = 0,
    Api = 1,
    Webhook = 2,
    HostedStrategy = 3,
    Flow = 4,
    Agent = 5,
    ActionCenter = 6,
    KillSwitch = 7,
    StrategyModule = 8,
    SquareOff = 9,
    Gtt = 10,
}

public enum OrderOutcome
{
    Accepted = 0,
    Refused = 1,
    Queued = 2,
    Failed = 3,
}

/// <summary>Immutable record of every order decision: accepted, refused, queued or failed.</summary>
public sealed class OrderAuditLog : IImmutableAuditRecord
{
    public long Id { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public DateTime OccurredAtIst { get; init; }
    public Guid? AccountId { get; init; }
    public string? AccountType { get; init; }
    public string Environment { get; init; } = "";
    public OrderSource Source { get; init; }
    public OrderOutcome Outcome { get; init; }
    public string Action { get; init; } = "";
    public string? Symbol { get; init; }
    public string? Exchange { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? Price { get; init; }
    public string? Product { get; init; }
    public string? PriceType { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? Reason { get; init; }
    public string? RequestJson { get; init; }
    public string? CorrelationId { get; init; }
    public string? Actor { get; init; }
}
