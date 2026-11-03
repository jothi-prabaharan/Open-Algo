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

    public TestHost(string environment)
    {
        var root = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var cs = new NpgsqlConnectionStringBuilder(root) { Database = $"ep_trade_test_{Guid.NewGuid():N}" }.ConnectionString;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("Database:ConnectionString", cs);
            b.UseSetting("Management:AdminToken", AdminToken);
        });
        Admin = _factory.CreateClient();
        Admin.DefaultRequestHeaders.Add("X-Admin-Token", AdminToken);
    }

    public HttpClient Admin { get; }

    public IServiceProvider Services => _factory.Services;

    public HttpClient Anonymous() => _factory.CreateClient();

    public T Get<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    public EpTradeDbContext NewDb() => _factory.Services.GetRequiredService<IDbContextFactory<EpTradeDbContext>>().CreateDbContext();

    public async Task<Guid> CreateAccountAsync(AccountType type, string name, decimal? maxLoss = null, int? maxQty = null, decimal? maxValue = null)
    {
        var created = await Admin.PostAsJsonAsync("/api/v1/accounts", new { display_name = name, broker = type == AccountType.PAPER ? "sandbox" : "zerodha", client_id = name, account_type = type.ToString() });
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

    public async ValueTask DisposeAsync()
    {
        await using (var db = NewDb())
        {
            await db.Database.EnsureDeletedAsync();
        }

        Admin.Dispose();
        await _factory.DisposeAsync();
    }
}
