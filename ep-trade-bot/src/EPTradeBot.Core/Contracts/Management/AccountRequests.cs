using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.Management;

/// <summary>POST /api/v1/accounts: register a broker account. The type is required; nothing defaults to LIVE.</summary>
public sealed class CreateBrokerAccountRequest
{
    [Required, StringLength(64, MinimumLength = 1)] [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [Required, StringLength(32, MinimumLength = 2), RegularExpression("^[a-z0-9_]+$")] [JsonPropertyName("broker")] public string Broker { get; set; } = "";
    [StringLength(64)] [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [Required(AllowEmptyStrings = false), RegularExpression("^(LIVE|PAPER)$", ErrorMessage = "Account type must be LIVE or PAPER.")]
    [JsonPropertyName("account_type")] public string? AccountType { get; set; }
}

/// <summary>PUT /api/v1/accounts/{accountId}/risk</summary>
public sealed class UpdateAccountRiskSettingsRequest
{
    [Required, Range(typeof(decimal), "1", "1000000000", ErrorMessage = "Max daily loss must be between 1 and 1,00,00,00,000 INR.")]
    [JsonPropertyName("max_daily_loss")] public decimal? MaxDailyLoss { get; set; }

    [Required, Range(1, 10_000_000, ErrorMessage = "Max order quantity must be between 1 and 1,00,00,000.")]
    [JsonPropertyName("max_order_quantity")] public int? MaxOrderQuantity { get; set; }

    [Required, Range(typeof(decimal), "1", "100000000000", ErrorMessage = "Max position value must be between 1 and 10,000 crore INR.")]
    [JsonPropertyName("max_position_value")] public decimal? MaxPositionValue { get; set; }

    [JsonPropertyName("is_enabled")] public bool IsEnabled { get; set; } = true;
}

/// <summary>POST /api/v1/killswitch/account/{accountId}</summary>
public sealed class AccountKillSwitchRequest
{
    /// <summary>True engages the switch (blocks, cancels, squares off); false releases it.</summary>
    [Required] [JsonPropertyName("engage")] public bool? Engage { get; set; }
    [StringLength(500)] [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>POST /api/v1/killswitch/global. Requires the typed confirmation phrase.</summary>
public sealed class GlobalKillSwitchRequest
{
    public const string ConfirmationPhrase = "KILL ALL";

    [Required(AllowEmptyStrings = false), RegularExpression("^KILL ALL$", ErrorMessage = "Type KILL ALL to confirm.")]
    [JsonPropertyName("confirmation")] public string? Confirmation { get; set; }
    [StringLength(500)] [JsonPropertyName("reason")] public string? Reason { get; set; }
}
