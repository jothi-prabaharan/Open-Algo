using System.Net.Http.Json;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.MarketData;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EPTradeBot.IntegrationTests;

/// <summary>
/// Boots the real API host against an empty PostgreSQL database and proves that
/// startup alone (MigrateAsync) creates the six schemas and that the mapped model
/// round-trips through them.
/// </summary>
public sealed class StartupMigrationTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private WebApplicationFactory<Program>? _factory;

    public StartupMigrationTests()
    {
        var root = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable) ?? "Host=unused";
        _connectionString = new NpgsqlConnectionStringBuilder(root) { Database = $"ep_trade_test_{Guid.NewGuid():N}" }.ConnectionString;
    }

    public Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable))) return Task.CompletedTask;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Database:ConnectionString", _connectionString);
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is null) return;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<EpTradeDbContext>().Database.EnsureDeletedAsync();
        }

        await _factory.DisposeAsync();
    }

    [PostgresFact]
    public async Task Startup_applies_every_migration_to_an_empty_database()
    {
        using var client = _factory!.CreateClient();
        var env = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/environment");
        Assert.Equal("Development", env!["environment"].ToString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EpTradeDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }

    [PostgresFact]
    public async Task Accounts_risk_settings_and_candles_round_trip_through_their_schemas()
    {
        using var client = _factory!.CreateClient();
        await client.GetAsync("/healthz");

        var accountId = Guid.CreateVersion7();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EpTradeDbContext>();
            db.BrokerAccounts.Add(new BrokerAccount
            {
                Id = accountId, DisplayName = "Paper desk", Broker = "sandbox", AccountType = AccountType.PAPER,
                RiskSetting = new AccountRiskSetting { MaxDailyLoss = 25_000m, MaxOrderQuantity = 1_800, MaxPositionValue = 5_000_000m },
            });
            db.Candles.Add(new Candle
            {
                Symbol = "SBIN", Exchange = "NSE", Interval = "1m",
                Ts = new DateTime(2026, 11, 2, 3, 45, 0, DateTimeKind.Utc), Open = 800, High = 801, Low = 799, Close = 800.5, Volume = 1000,
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EpTradeDbContext>();
            var account = await db.BrokerAccounts.Include(a => a.RiskSetting).SingleAsync(a => a.Id == accountId);
            Assert.Equal(AccountType.PAPER, account.AccountType);
            Assert.Equal(1_800, account.RiskSetting!.MaxOrderQuantity);
            Assert.NotEqual(0u, account.Version);

            var bar = await db.Candles.SingleAsync(c => c.Symbol == "SBIN" && c.Ts >= new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(800.5, bar.Close);
        }
    }
}
