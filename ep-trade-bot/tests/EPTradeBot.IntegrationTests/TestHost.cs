using System.Net.Http.Json;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EPTradeBot.IntegrationTests;

/// <summary>A real API host on its own empty PostgreSQL database, in a chosen environment.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string AdminToken = "integration-test-admin-token-0000000000";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connectionString;

    public const string AppKey = "integration-test-app-key-000000000000000000";
    public const string ApiKeyPepper = "integration-test-api-key-pepper-0000000000";

    public TestHost(string environment, Action<IWebHostBuilder>? configure = null)
    {
        var root = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var cs = new NpgsqlConnectionStringBuilder(root) { Database = $"ep_trade_test_{Guid.NewGuid():N}" }.ConnectionString;
        _connectionString = cs;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("Database:ConnectionString", cs);
            b.UseSetting("Management:AdminToken", AdminToken);
            b.UseSetting("Security:AppKey", AppKey);
            b.UseSetting("Security:ApiKeyPepper", ApiKeyPepper);
            // Hosts run side by side in tests; each gets its own bus port.
            BusPort = FreePort();
            b.UseSetting("Streaming:BusPort", BusPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The scheduler stays off unless a test turns it on; it reads the real clock.
            b.UseSetting("Scheduling:Enabled", "false");
            b.UseSetting("Strategies:Enabled", "false");
            configure?.Invoke(b);
        });
        try
        {
            Admin = _factory.CreateClient();
        }
        catch
        {
            // Startup failed (the case some tests are about): still drop the database it created.
            DropDatabase().AsTask().GetAwaiter().GetResult();
            throw;
        }

        Admin.DefaultRequestHeaders.Add("X-Admin-Token", AdminToken);
    }

    public HttpClient Admin { get; }

    public int BusPort { get; private set; }

    public Microsoft.AspNetCore.TestHost.TestServer Server => _factory.Server;

    public static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public IServiceProvider Services => _factory.Services;

    public HttpClient Anonymous() => _factory.CreateClient();

    public T Get<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    public EpTradeDbContext NewDb() => _factory.Services.GetRequiredService<IDbContextFactory<EpTradeDbContext>>().CreateDbContext();

    public async Task<Guid> CreateAccountAsync(AccountType type, string name, decimal? maxLoss = null, int? maxQty = null, decimal? maxValue = null,
        string? broker = null, string? clientId = null)
    {
        var created = await Admin.PostAsJsonAsync("/api/v1/accounts", new
        {
            display_name = name,
            broker = broker ?? (type == AccountType.PAPER ? "sandbox" : "zerodha"),
            client_id = clientId ?? name,
            account_type = type.ToString(),
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["id"].ToString()!;
        if (maxLoss is not null)
        {
            var risk = await Admin.PutAsJsonAsync($"/api/v1/accounts/{id}/risk", new { max_daily_loss = maxLoss, max_order_quantity = maxQty, max_position_value = maxValue });
            risk.EnsureSuccessStatusCode();
        }

        return Guid.Parse(id);
    }

    public void SetQuote(string exchange, string symbol, decimal ltp) =>
        Get<QuoteBook>().Set(exchange, symbol, new EPTradeBot.Core.Trading.Quote(ltp));

    /// <summary>
    /// Built from the connection string alone, so a host that failed to start still drops its database.
    /// EF drops with <c>WITH (FORCE)</c>, which a non-superuser cannot use while a superuser session is on
    /// the database ("permission denied to terminate process"). PostgreSQL's autovacuum workers are such
    /// sessions and come and go on their own, most often under the load of a full test run, so that one
    /// refusal is retried for a few seconds while the worker finishes.
    /// </summary>
    private async ValueTask DropDatabase()
    {
        var options = new DbContextOptionsBuilder<EpTradeDbContext>().UseNpgsql(_connectionString).Options;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = new EpTradeDbContext(options);
                var dropped = await db.Database.EnsureDeletedAsync();
                // A test must not leave its database behind; say so here rather than let it pile up unseen.
                if (await db.Database.CanConnectAsync())
                    throw new InvalidOperationException($"Test database {new NpgsqlConnectionStringBuilder(_connectionString).Database} still exists after the drop (EnsureDeleted returned {dropped}).");
                return;
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42501" && attempt < 40)
            {
                await Task.Delay(250);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The host first, so its own connections (and its background services) are gone before the drop.
        Admin.Dispose();
        await _factory.DisposeAsync();
        Npgsql.NpgsqlConnection.ClearAllPools();
        await DropDatabase();
    }
}
