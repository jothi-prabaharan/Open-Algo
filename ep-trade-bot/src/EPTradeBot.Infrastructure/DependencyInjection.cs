using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Persistence.Interceptors;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.Infrastructure.Trading;
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

        services.AddOptions<ManagementOptions>().Bind(configuration.GetSection(ManagementOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        // One factory for short-lived contexts (the sandbox opens one per transaction attempt),
        // and a scoped context from the same factory for request handlers.
        services.AddDbContextFactory<EpTradeDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseNpgsql(db.ConnectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", Schemas.Core);
                npgsql.CommandTimeout(db.CommandTimeoutSeconds);
            });
            options.AddInterceptors(new AuditImmutabilitySaveChangesInterceptor(), new AuditImmutabilityCommandInterceptor());
        });
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<EpTradeDbContext>>().CreateDbContext());

        services.AddSingleton<QuoteBook>();
        services.AddSingleton<IQuoteSource>(sp => sp.GetRequiredService<QuoteBook>());
        services.AddSingleton<IFreezeQuantityLookup, FreezeQuantityLookup>();
        services.AddSingleton<ITradingGuardService, TradingGuardService>();
        services.AddSingleton<ILiveOrderRouter, UnavailableLiveOrderRouter>();
        services.AddSingleton<KeyedAsyncLock<Guid>>();
        services.AddSingleton<SandboxEngine>();
        services.AddSingleton<AuditTrail>();
        services.AddSingleton<IAccountRiskManager, AccountRiskManager>();
        services.AddSingleton<IOrderDispatcher, OrderDispatcher>();
        services.AddSingleton<KillSwitchService>();

        return services;
    }
}
