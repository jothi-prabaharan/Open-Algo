using System.Text.Json;
using System.Text.Json.Serialization;
using EPTradeBot.Api.Endpoints;
using EPTradeBot.Api.Security;
using EPTradeBot.Api.Streaming;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Infrastructure.Streaming;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();

// The WebSocket proxy has its own port (8765 by default), as in OpenAlgo, beside the app's own URLs.
var streaming = builder.Configuration.GetSection(StreamingOptions.Section).Get<StreamingOptions>() ?? new StreamingOptions();
if (streaming.Enabled)
{
    var urls = builder.Configuration["urls"] ?? "http://localhost:5000";
    builder.WebHost.UseUrls($"{urls};http://{streaming.WebSocketHost}:{streaming.WebSocketPort}");
}

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<AdminTokenCheck>();
builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<WebSocketProxy>();
builder.Services.AddSingleton<IMarketTickSink>(sp => sp.GetRequiredService<WebSocketProxy>());
builder.Services.AddSingleton<IOrderUpdateSink>(sp => sp.GetRequiredService<WebSocketProxy>());
builder.Services.AddSingleton<IOrderUpdateSink, SignalROrderSink>();
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
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// OpenAlgo's clients connect to ws://host:8765 at any path; the web app uses /ws on the main port.
app.Use(async (context, next) =>
{
    if (context.WebSockets.IsWebSocketRequest && (context.Connection.LocalPort == streaming.WebSocketPort || context.Request.Path == "/ws"))
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        // The connection lives until the client closes it or the app stops (RequestAborted is not tied to a WebSocket's life on every server).
        var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await context.RequestServices.GetRequiredService<WebSocketProxy>().RunAsync(socket, stopping);
        return;
    }

    await next(context);
});

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
v1.MapCredentials();
v1.MapActionCenter();
v1.MapOpenAlgoV1();
if (app.Environment.IsDevelopment()) v1.MapDevelopmentQuotes();

app.MapHub<OrdersHub>(OrdersHub.Path);
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

/// <summary>Entry point, exposed for integration tests.</summary>
public partial class Program;
