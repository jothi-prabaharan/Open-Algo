namespace EPTradeBot.Infrastructure.Persistence;

/// <summary>The six PostgreSQL schemas of the single EP Trade bot database.</summary>
public static class Schemas
{
    public const string Core = "core";
    public const string Sandbox = "sandbox";
    public const string Audit = "audit";
    public const string Latency = "latency";
    public const string Telemetry = "telemetry";
    public const string MarketData = "market_data";

    public static readonly IReadOnlyList<string> All = [Core, Sandbox, Audit, Latency, Telemetry, MarketData];
}
