// Generated from OpenAlgo table 'ag_provider_model' (database.agent_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>ag_provider_model</c>.</summary>
public sealed class AgProviderModel
{
    public int Id { get; set; }
    public string ProviderKind { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? BaseUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; } = false;
    public bool SupportsReasoning { get; set; } = false;
    public string DefaultReasoningEffort { get; set; } = "off";
    public bool SupportsVision { get; set; } = false;
    public bool ToolsUnreliable { get; set; } = false;
    public DateTime? LastTestedAt { get; set; }
    public bool? LastTestOk { get; set; }
    public string? LastTestError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
