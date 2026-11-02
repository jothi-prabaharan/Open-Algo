using EPTradeBot.Core.Configuration;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SandboxOptions>().Bind(configuration.GetSection(SandboxOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<StreamingOptions>().Bind(configuration.GetSection(StreamingOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.AddDbContext<EpTradeDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseNpgsql(db.ConnectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", Schemas.Core);
                npgsql.CommandTimeout(db.CommandTimeoutSeconds);
            });
        });

        return services;
    }
}
