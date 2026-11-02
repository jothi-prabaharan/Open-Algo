using System.Text;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.MarketData;
using EPTradeBot.Core.Entities.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.Persistence;

/// <summary>
/// The single EP Trade bot database: one PostgreSQL database, six schemas
/// (core, sandbox, audit, latency, telemetry, market_data). Every table is mapped
/// through an <see cref="IEntityTypeConfiguration{TEntity}"/> in this assembly.
/// </summary>
public sealed class EpTradeDbContext(DbContextOptions<EpTradeDbContext> options) : DbContext(options)
{
    public DbSet<BrokerAccount> BrokerAccounts => Set<BrokerAccount>();
    public DbSet<AccountRiskSetting> AccountRiskSettings => Set<AccountRiskSetting>();
    public DbSet<HostedStrategy> HostedStrategies => Set<HostedStrategy>();
    public DbSet<OrderAuditLog> OrderAuditLogs => Set<OrderAuditLog>();
    public DbSet<KillSwitchEvent> KillSwitchEvents => Set<KillSwitchEvent>();
    public DbSet<ActionCenterDecision> ActionCenterDecisions => Set<ActionCenterDecision>();
    public DbSet<BusLaneStatus> BusLaneStatuses => Set<BusLaneStatus>();
    public DbSet<WebSocketProxyConnection> WebSocketProxyConnections => Set<WebSocketProxyConnection>();
    public DbSet<Candle> Candles => Set<Candle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schemas.Core);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EpTradeDbContext).Assembly);
        ApplySnakeCaseColumnNames(modelBuilder);
        ApplyLowerCaseConstraintNames(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // DateTime maps to timestamptz and must be UTC; IST wall-clock columns opt out explicitly.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    /// <summary>
    /// Columns without an explicit name get the snake_case form of the property name,
    /// so new tables follow the same naming as the ported OpenAlgo tables.
    /// </summary>
    private static void ApplySnakeCaseColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }
            }
        }
    }

    /// <summary>Primary keys, foreign keys and indexes are named in lower case, like everything else in the database.</summary>
    private static void ApplyLowerCaseConstraintNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var key in entity.GetKeys())
            {
                if (key.GetName() is { } name) key.SetName(name.ToLowerInvariant());
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.GetConstraintName() is { } name) fk.SetConstraintName(name.ToLowerInvariant());
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName() is { } name) index.SetDatabaseName(name.ToLowerInvariant());
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]) && i > 0 && char.IsUpper(name[i - 1]);
                if (prevLower || nextLower) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
