// Generated from OpenAlgo table 'ag_secret' (database.agent_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>ag_secret</c>.</summary>
public sealed class AgSecret
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime? LastUsedAt { get; set; }
}
