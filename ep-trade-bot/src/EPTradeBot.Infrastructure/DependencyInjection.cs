using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Infrastructure.Brokers;
using EPTradeBot.Infrastructure.OpenAlgoApi;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.Infrastructure.Streaming;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Persistence.Interceptors;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SandboxOptions>().Bind(configuration.GetSection(SandboxOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<StreamingOptions>().Bind(configuration.GetSection(StreamingOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<BrokerOptions>().Bind(configuration.GetSection(BrokerOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
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
        // One clock for code that asks what day it is (a broker's "today's orders"), replaceable in tests.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TokenProtector>();
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<BrokerSessionFactory>();
        services.AddSingleton<ISymbolMaster, EfSymbolMaster>();
        BrokerRegistry.RegisterPlugins(services);
        services.AddSingleton<IMarketSessions, MarketSessionLookup>();
        services.AddSingleton<IBrokerRegistry, BrokerRegistry>();
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(http => http.ConfigureHttpClient((sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<BrokerOptions>>().Value.TimeoutSeconds)));
        services.AddSingleton<ILiveOrderRouter, BrokerLiveOrderRouter>();
        services.AddSingleton<KeyedAsyncLock<string>>();
        services.AddSingleton<OpenAlgoApiService>();

        services.AddSingleton<OrderEvents>();
        services.AddSingleton<MarketDataPublisher>();
        services.AddSingleton<SandboxTickMatcher>();
        services.AddSingleton<IMarketTickSink>(sp => sp.GetRequiredService<SandboxTickMatcher>());
        services.AddSingleton<MarketDataBus>();
        if (configuration.GetValue(StreamingOptions.Section + ":Enabled", true))
        {
            services.AddHostedService(sp => sp.GetRequiredService<SandboxTickMatcher>());
            services.AddHostedService(sp => sp.GetRequiredService<MarketDataBus>());
        }
        services.AddSingleton<KeyedAsyncLock<Guid>>();
        services.AddSingleton<SandboxEngine>();
        services.AddSingleton<AuditTrail>();
        services.AddSingleton<IAccountRiskManager, AccountRiskManager>();
        services.AddSingleton<IOrderDispatcher, OrderDispatcher>();
        services.AddSingleton<KillSwitchService>();

        return services;
    }
}
