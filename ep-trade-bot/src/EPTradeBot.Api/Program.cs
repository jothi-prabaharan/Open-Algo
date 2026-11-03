using System.Text.Json;
using System.Text.Json.Serialization;
using EPTradeBot.Api.Endpoints;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// Schema evolution is EF Core migrations only, applied before the first request is served.
await DatabaseMigrator.MigrateAsync(app.Services);

app.UseExceptionHandler();

var v1 = app.MapGroup("/api/v1").WithDataAnnotations();
v1.MapGet("/environment", async (ITradingGuardService guard, EpTradeDbContext db, CancellationToken ct) => Results.Ok(new
{
    product = "EP Trade bot",
    environment = guard.EnvironmentName,
    live_trading_allowed = guard.LiveTradingAllowed,
    locked_account_ids = guard.LiveTradingAllowed
        ? []
        : await db.BrokerAccounts.AsNoTracking().Where(a => a.AccountType == EPTradeBot.Core.Entities.Core.AccountType.LIVE).Select(a => a.Id).ToListAsync(ct),
}));
v1.MapManagement();
if (app.Environment.IsDevelopment()) v1.MapDevelopmentQuotes();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

/// <summary>Entry point, exposed for integration tests.</summary>
public partial class Program;
