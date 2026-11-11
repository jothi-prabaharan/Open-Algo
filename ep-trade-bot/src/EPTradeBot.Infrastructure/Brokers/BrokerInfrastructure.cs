using System.Reflection;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure.Brokers;

/// <summary>Every <see cref="IBrokerPlugin"/> in this assembly, found at startup; only those named in Brokers:Enabled are offered.</summary>
public sealed class BrokerRegistry : IBrokerRegistry
{
    private readonly Dictionary<string, IBrokerPlugin> enabled;

    public BrokerRegistry(IEnumerable<IBrokerPlugin> plugins, IOptions<BrokerOptions> options)
    {
        var wanted = new HashSet<string>(options.Value.Enabled, StringComparer.OrdinalIgnoreCase);
        enabled = plugins.Where(p => wanted.Contains(p.Name)).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IBrokerPlugin> Enabled => enabled.Values;

    public IBrokerPlugin? Find(string broker) => enabled.GetValueOrDefault(broker);

    public T? Get<T>(string broker) where T : class, IBrokerPlugin => Find(broker) as T;

    /// <summary>Registers every concrete plugin type in the assembly.</summary>
    public static void RegisterPlugins(IServiceCollection services)
    {
        var types = typeof(BrokerRegistry).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IBrokerPlugin).IsAssignableFrom(t)
                && t.GetCustomAttribute<BrokerPluginAttribute>() is not null);
        foreach (var type in types)
        {
            services.AddSingleton(type);
            services.AddSingleton(sp => (IBrokerPlugin)sp.GetRequiredService(type));
        }
    }
}

/// <summary>Marks a class as a broker plugin to be discovered at startup.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BrokerPluginAttribute : Attribute;

/// <summary>Builds a <see cref="BrokerSession"/> for one call: decrypts the account's token and reads the broker's keys now.</summary>
public sealed class BrokerSessionFactory(TokenProtector protector, IOptionsMonitor<BrokerOptions> options)
{
    public const string AuthTokenField = "auth_token";
    public const string FeedTokenField = "feed_token";

    /// <summary>The session, or null when the account has no usable broker token (not logged in, or the token cannot be read).</summary>
    public BrokerSession? Create(BrokerAccount account)
    {
        var token = protector.Unprotect(account.Id, AuthTokenField, account.AuthTokenEncrypted);
        if (string.IsNullOrEmpty(token)) return null;
        var feed = protector.Unprotect(account.Id, FeedTokenField, account.FeedTokenEncrypted);
        var keys = options.CurrentValue.Keys.TryGetValue(account.Broker, out var k)
            ? new Dictionary<string, string>(k, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new BrokerSession(account.Id, account.Broker, token, feed, account.ClientId, keys);
    }

    /// <summary>The broker's application keys from configuration, as a session carries them.</summary>
    public IReadOnlyDictionary<string, string> KeysFor(string broker) =>
        options.CurrentValue.Keys.TryGetValue(broker, out var k) ? new Dictionary<string, string>(k, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pause between a broker's history calls: the configured one, else the broker's own limit.</summary>
    public TimeSpan HistoryGap(string broker, TimeSpan fallback) =>
        options.CurrentValue.HistoryGapMs.TryGetValue(broker, out var ms) ? TimeSpan.FromMilliseconds(ms) : fallback;

    /// <summary>The wait before a retry: the configured one, else the broker's own backoff.</summary>
    public TimeSpan RetryBackoff(string broker, TimeSpan fallback) =>
        options.CurrentValue.RetryBackoffMs.TryGetValue(broker, out var ms) ? TimeSpan.FromMilliseconds(ms) : fallback;

    public string BaseUrl(string broker, string fallback) =>
        options.CurrentValue.BaseUrls.TryGetValue(broker, out var url) && !string.IsNullOrWhiteSpace(url) ? url.TrimEnd('/') : fallback;
}

/// <summary>Master-contract lookups over <c>core.symtoken</c>, scoped to the broker.</summary>
public sealed class EfSymbolMaster(IDbContextFactory<EpTradeDbContext> contexts) : ISymbolMaster
{
    public async Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<Core.Entities.Legacy.Core.SymToken>().AsNoTracking()
            .Where(s => s.Broker == broker && s.Symbol == symbol && s.Exchange == exchange)
            .OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        return Map(row);
    }

    public async Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<Core.Entities.Legacy.Core.SymToken>().AsNoTracking()
            .Where(s => s.Broker == broker && s.Brsymbol == brokerSymbol && s.Exchange == exchange)
            .OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        return Map(row);
    }

    public async Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<Core.Entities.Legacy.Core.SymToken>().AsNoTracking()
            .Where(s => s.Broker == broker && s.Token == token && s.Exchange == exchange)
            .OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        return Map(row);
    }

    private static SymbolRecord? Map(Core.Entities.Legacy.Core.SymToken? r) => r is null ? null : new SymbolRecord(
        r.Symbol, r.Brsymbol, r.Exchange ?? "", r.Brexchange ?? "", r.Token ?? "", r.Name, r.Expiry, r.Strike, r.Lotsize ?? 1, r.Instrumenttype, r.TickSize, r.ContractValue);
}
