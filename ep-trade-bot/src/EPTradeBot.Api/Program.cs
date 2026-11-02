using EPTradeBot.Api.Validation;
using EPTradeBot.Infrastructure;
using EPTradeBot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

// Schema evolution is EF Core migrations only, applied before the first request is served.
await DatabaseMigrator.MigrateAsync(app.Services);

app.UseExceptionHandler();

var v1 = app.MapGroup("/api/v1").WithDataAnnotations();
v1.MapGet("/environment", (IHostEnvironment env) => Results.Ok(new
{
    product = "EP Trade bot",
    environment = env.EnvironmentName,
    liveTradingAllowed = env.IsProduction(),
}));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

/// <summary>Entry point, exposed for integration tests.</summary>
public partial class Program;
