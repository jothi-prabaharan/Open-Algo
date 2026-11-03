using EPTradeBot.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure.Persistence;

/// <summary>Applies pending EF Core migrations before the host starts serving requests.</summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.MigrateOnStartup) return;

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EpTradeDbContext>();

                // Create the database and the migrations history table first, through EF's own
                // services. Otherwise MigrateAsync probes a history table that does not exist yet
                // and logs a failed command on every fresh install, which reads as a fault.
                var creator = db.GetService<IRelationalDatabaseCreator>();
                if (!await creator.ExistsAsync(cancellationToken)) await creator.CreateAsync(cancellationToken);
                await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);

                await db.Database.MigrateAsync(cancellationToken);
                var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
                logger.LogInformation("Database schema is up to date at migration {Latest} ({Count} applied).", applied.LastOrDefault(), applied.Count);
                await Seed.FreezeQuantitySeed.SeedAsync(db, logger, cancellationToken);
                await Seed.MarketCalendarSeed.SeedAsync(db, logger, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < options.MigrationRetryCount && ex is not OperationCanceledException)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                logger.LogWarning(ex, "The database is not reachable yet (attempt {Attempt}); retrying in {Delay}.", attempt, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
