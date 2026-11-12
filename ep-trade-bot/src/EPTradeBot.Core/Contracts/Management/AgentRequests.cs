using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.Management;

/// <summary>PUT /api/v1/agent/provider: choose the language model service. A key left out keeps the one already saved.</summary>
public sealed class SetAgentProviderRequest
{
    [Required, Url, StringLength(300, MinimumLength = 8)] [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 1)] [JsonPropertyName("model")] public string Model { get; set; } = "";
    [StringLength(2000)] [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
}

public sealed class AgentMessage
{
    [Required, RegularExpression("^(user|assistant)$", ErrorMessage = "A message is from the user or the assistant.")]
    [JsonPropertyName("role")] public string Role { get; set; } = "";

    [Required, StringLength(8000, MinimumLength = 1)] [JsonPropertyName("content")] public string Content { get; set; } = "";
}

/// <summary>POST /api/v1/agent/chat: the conversation so far, ending with what the trader just asked.</summary>
public sealed class AgentChatRequest
{
    [Required] [JsonPropertyName("account_id")] public Guid? AccountId { get; set; }

    [Required, MinLength(1), MaxLength(40)] [JsonPropertyName("messages")] public List<AgentMessage> Messages { get; set; } = [];
}
