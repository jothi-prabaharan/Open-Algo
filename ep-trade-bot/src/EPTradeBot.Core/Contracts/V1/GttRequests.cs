using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.V1;

/// <summary>
/// Flat GTT shape shared by place and modify. SINGLE takes exactly one of
/// <see cref="TriggerPriceSl"/> / <see cref="TriggerPriceTg"/>; OCO takes all four
/// trigger and limit fields with the stoploss trigger below the target trigger.
/// </summary>
public abstract class GttOrderRequest : StrategyRequest, IValidatableObject
{
    [Required, RegularExpression(ApiPatterns.GttTriggerType, ErrorMessage = "Must be 'SINGLE' or 'OCO'.")] [JsonPropertyName("trigger_type")] public string TriggerType { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.GttProduct, ErrorMessage = "GTT supports only CNC (delivery) or NRML (overnight F&O); MIS is intraday-only.")]
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [Range(0, double.MaxValue, MinimumIsExclusive = true, ErrorMessage = "Quantity must be a positive number.")] [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [RegularExpression(ApiPatterns.GttPriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "LIMIT";
    [Required, Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal? Price { get; set; }
    [Range(0, double.MaxValue, ErrorMessage = "triggerprice_sl must be non-negative.")] [JsonPropertyName("triggerprice_sl")] public decimal? TriggerPriceSl { get; set; } = 0;
    [Range(0, double.MaxValue, ErrorMessage = "triggerprice_tg must be non-negative.")] [JsonPropertyName("triggerprice_tg")] public decimal? TriggerPriceTg { get; set; } = 0;
    [JsonPropertyName("stoploss")] public decimal? StopLoss { get; set; }
    [JsonPropertyName("target")] public decimal? Target { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var sl = TriggerPriceSl ?? 0;
        var tg = TriggerPriceTg ?? 0;
        if (string.Equals(TriggerType, "OCO", StringComparison.OrdinalIgnoreCase))
        {
            if (sl == 0) yield return new("Required for OCO (stoploss trigger).", ["triggerprice_sl"]);
            if ((StopLoss ?? 0) == 0) yield return new("Required for OCO (stoploss leg limit).", ["stoploss"]);
            if (tg == 0) yield return new("Required for OCO (target trigger).", ["triggerprice_tg"]);
            if ((Target ?? 0) == 0) yield return new("Required for OCO (target leg limit).", ["target"]);
            if (sl > 0 && tg > 0 && sl >= tg)
                yield return new("Stoploss trigger must be less than target trigger (triggerprice_tg).", ["triggerprice_sl"]);
        }
        else if (string.Equals(TriggerType, "SINGLE", StringComparison.OrdinalIgnoreCase) && sl <= 0 && tg <= 0)
        {
            yield return new("SINGLE GTT requires a positive triggerprice_sl or triggerprice_tg.", ["triggerprice_sl"]);
        }

        foreach (var r in QuantityRules.WholeUnlessCrypto(Exchange, Quantity)) yield return r;
    }
}

/// <summary>POST /api/v1/placegttorder</summary>
public sealed class PlaceGttOrderRequest : GttOrderRequest
{
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}

/// <summary>POST /api/v1/modifygttorder (full replacement).</summary>
public sealed class ModifyGttOrderRequest : GttOrderRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("trigger_id")] public string TriggerId { get; set; } = "";
}

/// <summary>POST /api/v1/cancelgttorder</summary>
public sealed class CancelGttOrderRequest : StrategyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("trigger_id")] public string TriggerId { get; set; } = "";
}

/// <summary>POST /api/v1/gttorderbook</summary>
public sealed class GttOrderBookRequest : ApiKeyRequest
{
    [RegularExpression("^(active|all)$")] [JsonPropertyName("status")] public string Status { get; set; } = "active";
}
