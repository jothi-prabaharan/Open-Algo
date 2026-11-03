using System.Data.Common;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Entities.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EPTradeBot.Infrastructure.Persistence.Interceptors;

/// <summary>Thrown when anything tries to change or remove an immutable audit record.</summary>
public sealed class AuditRecordImmutableException(string message) : InvalidOperationException(message);

/// <summary>Refuses to save a tracked audit record as modified or deleted.</summary>
public sealed class AuditImmutabilitySaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Check(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries<IImmutableAuditRecord>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new AuditRecordImmutableException($"Audit records cannot be changed or deleted ({entry.Metadata.GetTableName()}).");
        }
    }
}

/// <summary>
/// Refuses UPDATE, DELETE and TRUNCATE commands against the immutable audit tables.
/// This catches what the change tracker never sees: ExecuteUpdate and ExecuteDelete.
/// It reads the command EF Core generated; it never writes SQL.
/// </summary>
public sealed partial class AuditImmutabilityCommandInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    internal static bool Targets(string commandText) => Mutation().IsMatch(commandText);

    private static void Check(DbCommand command)
    {
        if (Targets(command.CommandText))
            throw new AuditRecordImmutableException("Audit records cannot be changed or deleted.");
    }

    [GeneratedRegex("""(^|;)\s*(UPDATE|DELETE\s+FROM|TRUNCATE(\s+TABLE)?)\s+(ONLY\s+)?"?audit"?\."?(order_audit_log|kill_switch_events|action_center_decisions)"?\b""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Mutation();
}
