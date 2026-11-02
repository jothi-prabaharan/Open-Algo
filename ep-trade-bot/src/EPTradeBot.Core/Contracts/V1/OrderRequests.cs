using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.V1;

/// <summary>Every /api/v1 request carries the API key in its body (external platforms cannot set headers).</summary>
public abstract class ApiKeyRequest
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(256, MinimumLength = 1)]
    [JsonPropertyName("apikey")]
    public string ApiKey { get; set; } = "";
}

public abstract class StrategyRequest : ApiKeyRequest
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(100, MinimumLength = 1)]
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "";
}

/// <summary>Shared fractional-quantity rule: only crypto exchanges accept a fractional quantity.</summary>
internal static class QuantityRules
{
    public static IEnumerable<ValidationResult> WholeUnlessCrypto(string? exchange, decimal quantity, string member = "quantity")
    {
        if (exchange is not null && !ApiPatterns.CryptoExchanges.Contains(exchange) && quantity != decimal.Truncate(quantity))
        {
            yield return new ValidationResult(
                $"Fractional quantity ({quantity}) is not allowed for non-crypto exchanges.", [member]);
        }
    }
}

/// <summary>POST /api/v1/placeorder</summary>
public sealed class PlaceOrderRequest : StrategyRequest, IValidatableObject
{
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Range(0, double.MaxValue, MinimumIsExclusive = true, ErrorMessage = "Quantity must be a positive number.")]
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "MARKET";
    [RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "MIS";
    [Range(0, double.MaxValue, ErrorMessage = "Price must be a non-negative number.")] [JsonPropertyName("price")] public decimal Price { get; set; }
    [Range(0, double.MaxValue, ErrorMessage = "Trigger price must be a non-negative number.")] [JsonPropertyName("trigger_price")] public decimal TriggerPrice { get; set; }
    [Range(0, int.MaxValue, ErrorMessage = "Disclosed quantity must be a non-negative integer.")] [JsonPropertyName("disclosed_quantity")] public int DisclosedQuantity { get; set; }
    [JsonPropertyName("underlying_ltp")] public decimal? UnderlyingLtp { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        QuantityRules.WholeUnlessCrypto(Exchange, Quantity);
}

/// <summary>POST /api/v1/placesmartorder</summary>
public sealed class PlaceSmartOrderRequest : StrategyRequest, IValidatableObject
{
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Range(0, double.MaxValue, ErrorMessage = "Quantity must be a non-negative number.")] [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [Required] [JsonPropertyName("position_size")] public decimal? PositionSize { get; set; }
    [RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "MARKET";
    [RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "MIS";
    [Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal Price { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("trigger_price")] public decimal TriggerPrice { get; set; }
    [Range(0, int.MaxValue)] [JsonPropertyName("disclosed_quantity")] public int DisclosedQuantity { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        QuantityRules.WholeUnlessCrypto(Exchange, Quantity);
}

/// <summary>POST /api/v1/modifyorder (every field required, as in OpenAlgo).</summary>
public sealed class ModifyOrderRequest : StrategyRequest, IValidatableObject
{
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("orderid")] public string OrderId { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "";
    [Required, Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal? Price { get; set; }
    [Required, Range(0, double.MaxValue, MinimumIsExclusive = true, ErrorMessage = "Quantity must be a positive number.")] [JsonPropertyName("quantity")] public decimal? Quantity { get; set; }
    [Required, Range(0, int.MaxValue)] [JsonPropertyName("disclosed_quantity")] public int? DisclosedQuantity { get; set; }
    [Required, Range(0, double.MaxValue)] [JsonPropertyName("trigger_price")] public decimal? TriggerPrice { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        QuantityRules.WholeUnlessCrypto(Exchange, Quantity ?? 0);
}

/// <summary>POST /api/v1/cancelorder</summary>
public sealed class CancelOrderRequest : StrategyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("orderid")] public string OrderId { get; set; } = "";
}

/// <summary>POST /api/v1/closeposition</summary>
public sealed class ClosePositionRequest : StrategyRequest;

/// <summary>POST /api/v1/cancelallorder</summary>
public sealed class CancelAllOrderRequest : StrategyRequest;

public sealed class BasketOrderItem : IValidatableObject
{
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Range(0, double.MaxValue, MinimumIsExclusive = true, ErrorMessage = "Quantity must be a positive number.")] [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "MARKET";
    [RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "MIS";
    [Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal Price { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("trigger_price")] public decimal TriggerPrice { get; set; }
    [Range(0, int.MaxValue)] [JsonPropertyName("disclosed_quantity")] public int DisclosedQuantity { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        QuantityRules.WholeUnlessCrypto(Exchange, Quantity);
}

/// <summary>POST /api/v1/basketorder</summary>
public sealed class BasketOrderRequest : StrategyRequest
{
    [Required, MinLength(1, ErrorMessage = "Orders must contain at least 1 item.")]
    [JsonPropertyName("orders")] public List<BasketOrderItem> Orders { get; set; } = [];
}

/// <summary>POST /api/v1/splitorder</summary>
public sealed class SplitOrderRequest : StrategyRequest, IValidatableObject
{
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Range(0, double.MaxValue, MinimumIsExclusive = true, ErrorMessage = "Total quantity must be a positive number.")] [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Split size must be a positive integer.")] [JsonPropertyName("splitsize")] public int SplitSize { get; set; }
    [RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "MARKET";
    [RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "MIS";
    [Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal Price { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("trigger_price")] public decimal TriggerPrice { get; set; }
    [Range(0, int.MaxValue)] [JsonPropertyName("disclosed_quantity")] public int DisclosedQuantity { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        QuantityRules.WholeUnlessCrypto(Exchange, Quantity);
}

/// <summary>POST /api/v1/optionsorder</summary>
public sealed class OptionsOrderRequest : StrategyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("underlying")] public string Underlying { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [RegularExpression(ApiPatterns.ExpiryDdMmmYy)] [JsonPropertyName("expiry_date")] public string? ExpiryDate { get; set; }
    [Range(1, int.MaxValue)] [JsonPropertyName("strike_int")] public int? StrikeInterval { get; set; }
    [Required, RegularExpression(ApiPatterns.OptionOffset, ErrorMessage = "Offset must be ATM, ITM1-ITM50, or OTM1-OTM50")] [JsonPropertyName("offset")] public string Offset { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.OptionType)] [JsonPropertyName("option_type")] public string OptionType { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be a positive integer.")] [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [Range(0, int.MaxValue)] [JsonPropertyName("splitsize")] public int? SplitSize { get; set; } = 0;
    [RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "MARKET";
    [RegularExpression(ApiPatterns.OptionsProduct)] [JsonPropertyName("product")] public string Product { get; set; } = "MIS";
    [Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal Price { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("trigger_price")] public decimal TriggerPrice { get; set; }
    [Range(0, int.MaxValue)] [JsonPropertyName("disclosed_quantity")] public int DisclosedQuantity { get; set; }
}

public sealed class OptionsMultiOrderLeg
{
    [Required, RegularExpression(ApiPatterns.OptionOffset, ErrorMessage = "Offset must be ATM, ITM1-ITM50, or OTM1-OTM50")] [JsonPropertyName("offset")] public string Offset { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.OptionType)] [JsonPropertyName("option_type")] public string OptionType { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be a positive integer.")] [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [Range(0, int.MaxValue)] [JsonPropertyName("splitsize")] public int? SplitSize { get; set; } = 0;
    [RegularExpression(ApiPatterns.ExpiryDdMmmYy)] [JsonPropertyName("expiry_date")] public string? ExpiryDate { get; set; }
    [RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "MARKET";
    [RegularExpression(ApiPatterns.OptionsProduct)] [JsonPropertyName("product")] public string Product { get; set; } = "MIS";
    [Range(0, double.MaxValue)] [JsonPropertyName("price")] public decimal Price { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("trigger_price")] public decimal TriggerPrice { get; set; }
    [Range(0, int.MaxValue)] [JsonPropertyName("disclosed_quantity")] public int DisclosedQuantity { get; set; }
}

/// <summary>POST /api/v1/optionsmultiorder</summary>
public sealed class OptionsMultiOrderRequest : StrategyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("underlying")] public string Underlying { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [RegularExpression(ApiPatterns.ExpiryDdMmmYy)] [JsonPropertyName("expiry_date")] public string? ExpiryDate { get; set; }
    [Range(1, int.MaxValue)] [JsonPropertyName("strike_int")] public int? StrikeInterval { get; set; }
    [Required, Length(1, 20, ErrorMessage = "Legs must contain 1 to 20 items.")] [JsonPropertyName("legs")] public List<OptionsMultiOrderLeg> Legs { get; set; } = [];
}

/// <summary>POST /api/v1/syntheticfuture</summary>
public sealed class SyntheticFutureRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("underlying")] public string Underlying { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.ExpiryDdMmmYy)] [JsonPropertyName("expiry_date")] public string ExpiryDate { get; set; } = "";
}

public sealed class MarginPosition
{
    [Required, StringLength(50, MinimumLength = 1, ErrorMessage = "Symbol must be between 1 and 50 characters.")] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Action)] [JsonPropertyName("action")] public string Action { get; set; } = "";
    /// <summary>A string, matching OpenAlgo's API contract.</summary>
    [Required, RegularExpression(@"^\d+(\.\d+)?$")] [JsonPropertyName("quantity")] public string Quantity { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Product)] [JsonPropertyName("product")] public string Product { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.PriceType)] [JsonPropertyName("pricetype")] public string PriceType { get; set; } = "";
    [RegularExpression(@"^\d+(\.\d+)?$")] [JsonPropertyName("price")] public string Price { get; set; } = "0";
    [RegularExpression(@"^\d+(\.\d+)?$")] [JsonPropertyName("trigger_price")] public string TriggerPrice { get; set; } = "0";
}

/// <summary>POST /api/v1/margin</summary>
public sealed class MarginCalculatorRequest : ApiKeyRequest
{
    [Required, Length(1, 50, ErrorMessage = "Positions must contain 1 to 50 items.")] [JsonPropertyName("positions")] public List<MarginPosition> Positions { get; set; } = [];
}
