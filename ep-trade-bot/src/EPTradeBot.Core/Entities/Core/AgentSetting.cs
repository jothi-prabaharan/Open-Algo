using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Entities.Core;

/// <summary>Which language model the assistant talks to. One row. The key is stored encrypted and is never read back.</summary>
public sealed class AgentSetting
{
    /// <summary>Always 1: there is one provider for the install.</summary>
    public int Id { get; set; } = 1;

    /// <summary>The base address of an OpenAI-compatible service, e.g. https://api.openai.com/v1.</summary>
    [Required, StringLength(300, MinimumLength = 8)]
    public string Endpoint { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 1)]
    public string Model { get; set; } = "";

    /// <summary>The API key, encrypted with the application key.</summary>
    [StringLength(2000)]
    public string? ProtectedApiKey { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
