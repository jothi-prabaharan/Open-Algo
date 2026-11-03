using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.V1;

public sealed class SymbolExchangePair
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
}

/// <summary>POST /api/v1/quotes</summary>
public sealed class QuotesRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
}

/// <summary>POST /api/v1/multiquotes</summary>
public sealed class MultiQuotesRequest : ApiKeyRequest
{
    [Required, MinLength(1)] [JsonPropertyName("symbols")] public List<SymbolExchangePair> Symbols { get; set; } = [];
}

/// <summary>POST /api/v1/history</summary>
public sealed class HistoryRequest : ApiKeyRequest, IValidatableObject
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.HistoryInterval)] [JsonPropertyName("interval")] public string Interval { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.IsoDate)] [JsonPropertyName("start_date")] public string StartDate { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.IsoDate)] [JsonPropertyName("end_date")] public string EndDate { get; set; } = "";
    [RegularExpression("^(api|db)$")] [JsonPropertyName("source")] public string Source { get; set; } = "api";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", out _)) yield return new("Not a valid date.", ["start_date"]);
        if (!DateOnly.TryParseExact(EndDate, "yyyy-MM-dd", out _)) yield return new("Not a valid date.", ["end_date"]);
    }
}

/// <summary>POST /api/v1/depth</summary>
public sealed class DepthRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
}

/// <summary>POST /api/v1/intervals</summary>
public sealed class IntervalsRequest : ApiKeyRequest;

/// <summary>POST /api/v1/symbol</summary>
public sealed class SymbolRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
}

/// <summary>GET/POST /api/v1/ticker/{symbol}</summary>
public sealed class TickerRequest : ApiKeyRequest
{
    /// <summary>Combined EXCHANGE:SYMBOL.</summary>
    [Required, StringLength(80, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.TickerInterval)] [JsonPropertyName("interval")] public string Interval { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.DateOrEpoch, ErrorMessage = "Field must be a string in 'YYYY-MM-DD' format or a numeric timestamp.")] [JsonPropertyName("from")] public string From { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.DateOrEpoch, ErrorMessage = "Field must be a string in 'YYYY-MM-DD' format or a numeric timestamp.")] [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("adjusted")] public bool Adjusted { get; set; } = true;
    [RegularExpression("^(asc|desc)$")] [JsonPropertyName("sort")] public string Sort { get; set; } = "asc";
}

/// <summary>POST /api/v1/search</summary>
public sealed class SearchRequest : ApiKeyRequest
{
    /// <summary>Blank text passes the schema, as in OpenAlgo, and the service answers it with its own message.</summary>
    [Required(AllowEmptyStrings = true), StringLength(100, MinimumLength = 1)] [JsonPropertyName("query")] public string Query { get; set; } = "";
    [RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string? Exchange { get; set; }
}

/// <summary>POST /api/v1/expiry</summary>
public sealed class ExpiryRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.FnoExchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, RegularExpression("^(futures|options)$")] [JsonPropertyName("instrumenttype")] public string InstrumentType { get; set; } = "";
}

/// <summary>POST /api/v1/optionsymbol</summary>
public sealed class OptionSymbolRequest : ApiKeyRequest
{
    /// <summary>Deprecated in OpenAlgo; accepted and ignored.</summary>
    [StringLength(100)] [JsonPropertyName("strategy")] public string? Strategy { get; set; }
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("underlying")] public string Underlying { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [RegularExpression(ApiPatterns.ExpiryDdMmmYy)] [JsonPropertyName("expiry_date")] public string? ExpiryDate { get; set; }
    [Range(1, int.MaxValue)] [JsonPropertyName("strike_int")] public int? StrikeInterval { get; set; }
    [Required, RegularExpression(ApiPatterns.OptionOffset, ErrorMessage = "Offset must be ATM, ITM1-ITM50, or OTM1-OTM50")] [JsonPropertyName("offset")] public string Offset { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.OptionType)] [JsonPropertyName("option_type")] public string OptionType { get; set; } = "";
}

/// <summary>POST /api/v1/optiongreeks</summary>
public sealed class OptionGreeksRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.FnoExchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Range(0, 100)] [JsonPropertyName("interest_rate")] public double? InterestRate { get; set; }
    [Range(0, double.MaxValue)] [JsonPropertyName("forward_price")] public double? ForwardPrice { get; set; }
    [StringLength(64)] [JsonPropertyName("underlying_symbol")] public string? UnderlyingSymbol { get; set; }
    [RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("underlying_exchange")] public string? UnderlyingExchange { get; set; }
    [RegularExpression(ApiPatterns.HourMinute)] [JsonPropertyName("expiry_time")] public string? ExpiryTime { get; set; }
}

public sealed class OptionSymbolItem
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.FnoExchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [StringLength(64)] [JsonPropertyName("underlying_symbol")] public string? UnderlyingSymbol { get; set; }
    [RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("underlying_exchange")] public string? UnderlyingExchange { get; set; }
}

/// <summary>POST /api/v1/multioptiongreeks</summary>
public sealed class MultiOptionGreeksRequest : ApiKeyRequest
{
    [Required, Length(1, 50)] [JsonPropertyName("symbols")] public List<OptionSymbolItem> Symbols { get; set; } = [];
    [Range(0, 100)] [JsonPropertyName("interest_rate")] public double? InterestRate { get; set; }
    [RegularExpression(ApiPatterns.HourMinute)] [JsonPropertyName("expiry_time")] public string? ExpiryTime { get; set; }
}

/// <summary>POST /api/v1/instruments</summary>
public sealed class InstrumentsRequest : ApiKeyRequest
{
    [RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string? Exchange { get; set; }
    [RegularExpression("^(json|csv)$")] [JsonPropertyName("format")] public string? Format { get; set; }
}

/// <summary>POST /api/v1/optionchain</summary>
public sealed class OptionChainRequest : ApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("underlying")] public string Underlying { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.Exchange)] [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
    [Required, RegularExpression(ApiPatterns.ExpiryDdMmmYy)] [JsonPropertyName("expiry_date")] public string ExpiryDate { get; set; } = "";
    [Range(1, 100)] [JsonPropertyName("strike_count")] public int? StrikeCount { get; set; }
    [JsonPropertyName("with_greeks")] public bool WithGreeks { get; set; }
    [Range(0, 100)] [JsonPropertyName("interest_rate")] public double? InterestRate { get; set; }
}

/// <summary>POST /api/v1/market/holidays</summary>
public sealed class MarketHolidaysRequest : ApiKeyRequest
{
    [Range(2020, 2050)] [JsonPropertyName("year")] public int? Year { get; set; }
}

/// <summary>POST /api/v1/market/timings</summary>
public sealed class MarketTimingsRequest : ApiKeyRequest
{
    [Required, RegularExpression(ApiPatterns.IsoDate)] [JsonPropertyName("date")] public string Date { get; set; } = "";
}
