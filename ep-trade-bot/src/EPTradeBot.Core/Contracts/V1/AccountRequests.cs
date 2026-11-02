using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.V1;

/// <summary>POST /api/v1/funds</summary>
public sealed class FundsRequest : ApiKeyRequest;

/// <summary>POST /api/v1/orderbook</summary>
public sealed class OrderbookRequest : ApiKeyRequest;

/// <summary>POST /api/v1/tradebook</summary>
public sealed class TradebookRequest : ApiKeyRequest;

/// <summary>POST /api/v1/positionbook</summary>
public sealed class PositionbookRequest : ApiKeyRequest;

/// <summary>POST /api/v1/holdings</summary>
public sealed class HoldingsRequest : ApiKeyRequest;

/// <summary>POST /api/v1/ping</summary>
public sealed class PingRequest : ApiKeyRequest;

/// <summary>POST /api/v1/analyzer</summary>
public sealed class AnalyzerRequest : ApiKeyRequest;

/// <summary>POST /api/v1/pnl/symbols</summary>
public sealed class PnlSymbolsRequest : ApiKeyRequest;

/// <summary>POST /api/v1/orderstatus</summary>
public sealed class OrderStatusRequest : StrategyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("orderid")] public string OrderId { get; set; } = "";
}

/// <summary>POST /api/v1/openposition</summary>
public sealed class OpenPositionRequest : StrategyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "";
}

/// <summary>POST /api/v1/analyzer/toggle</summary>
public sealed class AnalyzerToggleRequest : ApiKeyRequest
{
    [Required] [JsonPropertyName("mode")] public bool? Mode { get; set; }
}

/// <summary>POST /api/v1/chart: free-form chart preferences, keys passed through.</summary>
public sealed class ChartRequest : ApiKeyRequest
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Preferences { get; set; } = [];
}
