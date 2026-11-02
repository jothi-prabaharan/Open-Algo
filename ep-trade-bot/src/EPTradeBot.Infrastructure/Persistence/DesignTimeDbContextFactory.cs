using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EPTradeBot.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only. No database is contacted when adding a migration.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EpTradeDbContext>
{
    public EpTradeDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("EPTRADE_DESIGN_CONNECTION")
                 ?? "Host=localhost;Database=ep_trade_design;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<EpTradeDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations_history", Schemas.Core))
            .Options;
        return new EpTradeDbContext(options);
    }
}
