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

/// <summary>POST /api/v1/accounts/{id}/apikeys: issue an /api/v1 key for this account.</summary>
public sealed class CreateApiKeyRequest
{
    [Required(AllowEmptyStrings = false), StringLength(64, MinimumLength = 1)]
    public string Label { get; set; } = "";
}

/// <summary>
/// PUT /api/v1/accounts/{id}/broker-session: store the broker token for a LIVE account.
/// A stopgap until the broker login flows arrive with the web app (TG5).
/// </summary>
public sealed class SetBrokerSessionRequest
{
    [Required(AllowEmptyStrings = false), StringLength(4096, MinimumLength = 1)]
    public string AuthToken { get; set; } = "";

    [StringLength(4096)]
    public string? FeedToken { get; set; }
}

/// <summary>
/// POST /api/v1/accounts/{id}/broker-login/complete. A redirect broker (Zerodha, Paytm Money) sends the <c>request_token</c> its page returned and the
/// <c>state</c> the start gave; a credentials broker (Angel One) sends the typed <c>clientcode</c>, <c>password</c> and <c>totp</c>.
/// </summary>
public sealed class BrokerLoginCompleteRequest
{
    [StringLength(4096)] [JsonPropertyName("request_token")] public string? RequestToken { get; set; }

    [StringLength(4096)] [JsonPropertyName("state")] public string? State { get; set; }

    [StringLength(256)] [JsonPropertyName("clientcode")] public string? ClientCode { get; set; }

    [StringLength(256)] [JsonPropertyName("password")] public string? Password { get; set; }

    [StringLength(64)] [JsonPropertyName("totp")] public string? Totp { get; set; }
}

/// <summary>PUT /api/v1/accounts/{id}/apikeys/{keyId}/order-mode: <c>auto</c> sends this key's orders, <c>semi_auto</c> holds them in the Action Center.</summary>
public sealed class SetOrderModeRequest
{
    [Required, RegularExpression("^(auto|semi_auto)$", ErrorMessage = "Use auto or semi_auto.")]
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
}

/// <summary>POST /api/v1/action-center/orders/{id}/reject, and reject-all.</summary>
public sealed class RejectOrderRequest
{
    [StringLength(500)] [JsonPropertyName("reason")] public string? Reason { get; set; }

    [JsonPropertyName("account_id")] public Guid? AccountId { get; set; }
}

/// <summary>POST /api/v1/action-center/approve-all: every waiting order, or those of one account.</summary>
public sealed class ApproveAllRequest
{
    [JsonPropertyName("account_id")] public Guid? AccountId { get; set; }
}
