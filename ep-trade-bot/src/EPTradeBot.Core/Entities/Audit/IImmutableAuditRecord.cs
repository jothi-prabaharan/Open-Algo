namespace EPTradeBot.Core.Entities.Audit;

/// <summary>
/// Marks an append-only audit record. Once written it can be read, never
/// updated or deleted; the persistence layer refuses both.
/// </summary>
public interface IImmutableAuditRecord
{
    DateTime OccurredAtUtc { get; }

    /// <summary>The same instant as Asia/Kolkata wall-clock time.</summary>
    DateTime OccurredAtIst { get; }
}
