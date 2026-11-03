using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>
/// The endpoints that ask no broker anything, recorded on Zerodha
/// (tools/contract-fixtures/platform.fixtures.json): market holidays and timings against the
/// calendar EP Trade bot seeds at startup, chart preferences, and the sandbox day P&amp;L by symbol.
/// </summary>
public sealed class PlatformHost : BrokerContractHost
{
    public override string BrokerName => "zerodha";
    public override string FixtureFile => "platform.fixtures.json";
    public override bool WithSandboxAccount => true;
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>();
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string>();

    public override async Task PrepareAsync(JsonObject fixture)
    {
        await using var db = Host!.NewDb();
        if (fixture["chart_prefs"] is JsonObject prefs)
        {
            var user = SandboxEngine.UserId(AccountId);
            await db.Set<ChartPreference>().Where(p => p.UserId == user).ExecuteDeleteAsync();
            foreach (var (key, value) in prefs)
                db.Set<ChartPreference>().Add(new ChartPreference { UserId = user, Key = key, Value = value?.GetValue<string>() });
        }

        if (fixture["sandbox_positions"] is JsonArray positions)
        {
            var user = SandboxEngine.UserId(PaperAccountId);
            await db.Set<SandboxPosition>().Where(p => p.UserId == user).ExecuteDeleteAsync();
            foreach (var row in positions.OfType<JsonObject>())
            {
                db.Set<SandboxPosition>().Add(new SandboxPosition
                {
                    UserId = user, Symbol = row["symbol"]!.GetValue<string>(), Exchange = row["exchange"]!.GetValue<string>(), Product = row["product"]!.GetValue<string>(),
                    Quantity = row["quantity"]!.GetValue<int>(), AveragePrice = row["average_price"]!.GetValue<decimal>(), Ltp = row["ltp"]!.GetValue<decimal>(),
                    Pnl = row["pnl"]!.GetValue<decimal>(), TodayRealizedPnl = row["today_realized_pnl"]!.GetValue<decimal>(),
                    AccumulatedRealizedPnl = row["accumulated_realized_pnl"]!.GetValue<decimal>(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync();
    }
}

public sealed class PlatformContractTests(PlatformHost host, ITestOutputHelper output) : IClassFixture<PlatformHost>
{
    private const string KeyRefused = "Every /api/v1 call in EP Trade bot is made with an account's API key, and the key names the account. OpenAlgo answers the market calendar without checking the key.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["holidays_invalid_apikey"] = new(KeyRefused, f => ContractRunner.Expect(f, 403, ContractRunner.Error("Invalid openalgo apikey"))),
        ["timings_invalid_apikey"] = new(KeyRefused, f => ContractRunner.Expect(f, 403, ContractRunner.Error("Invalid openalgo apikey"))),
        ["chart_get_header_key"] = new(
            "OpenAlgo reads the key for GET /chart from the query string only. EP Trade bot also takes the X-API-KEY header, as on every other endpoint, which keeps the key out of URLs and their logs.",
            f => ContractRunner.Expect(f, 200, new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["tv_theme"] = "light" } })),
        ["chart_post_object_value"] = new(
            "OpenAlgo cannot store an object as a preference and answers 500. EP Trade bot stores its JSON text, as it does for a number.",
            f => ContractRunner.Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Preferences updated successfully" })),
        ["pnl_symbols_live_mode"] = new(
            "Same status. In EP Trade bot sandbox is a kind of account, not a mode of the install, so the refusal names the account and what to use instead.",
            f => ContractRunner.Expect(f, 400, ContractRunner.Error("P&L by symbol is only kept for sandbox accounts, and Contract is a live account. Use the API key of a sandbox account, or read the positions from /positionbook."))),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("platform.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}
