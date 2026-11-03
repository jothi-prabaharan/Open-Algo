using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EPTradeBot.Core.Contracts.V1;

namespace EPTradeBot.Core.Contracts.Management;

/// <summary>POST /api/v1/accounts/{accountId}/orders: a manual order against one account.</summary>
public sealed class ManualOrderRequest : IValidatableObject
{
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression("^(BUY|SELL)$")] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Range(1, 10_000_000, ErrorMessage = "Quantity must be a whole number above zero.")] [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "MARKET";
    [RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "MIS";
    [Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal Price { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("trigger_price")] public decimal TriggerPrice { get; set; }
    [StringLength(100)] [JsonPropertyName("strategy")] public string Strategy { get; set; } = "manual";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PriceType is "LIMIT" or "SL" && Price <= 0) yield return new("A limit price is required for LIMIT and SL orders.", ["price"]);
        if (PriceType is "SL" or "SL-M" && TriggerPrice <= 0) yield return new("A trigger price is required for SL and SL-M orders.", ["trigger_price"]);
    }
}

/// <summary>POST /api/v1/sandbox/quotes (Development only): set a quote while no market feed is connected.</summary>
public sealed class SetQuoteRequest
{
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Range(0.01, double.MaxValue)] [JsonPropertyName("ltp")] public decimal Ltp { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("bid")] public decimal? Bid { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("ask")] public decimal? Ask { get; set; }
}
