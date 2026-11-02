namespace EPTradeBot.Core.Entities.Audit;

public enum KillSwitchScope
{
    Account = 0,
    Global = 1,
}

/// <summary>Immutable record of a kill switch being engaged or released, with per-account results.</summary>
public sealed class KillSwitchEvent : IImmutableAuditRecord
{
    public long Id { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public DateTime OccurredAtIst { get; init; }
    public KillSwitchScope Scope { get; init; }
    public Guid? AccountId { get; init; }
    public bool Engaged { get; init; }
    public string Actor { get; init; } = "";
    public string? Reason { get; init; }

    /// <summary>True only when every cancel and square-off succeeded.</summary>
    public bool Completed { get; init; }

    /// <summary>Per-account outcome: orders cancelled, positions squared off, failures.</summary>
    public string ResultJson { get; init; } = "{}";
}
