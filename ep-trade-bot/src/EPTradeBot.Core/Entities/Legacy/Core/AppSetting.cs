// Generated from OpenAlgo table 'settings' (database.settings_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>settings</c>.</summary>
public sealed class AppSetting
{
    public int Id { get; set; }
    public bool? AnalyzeMode { get; set; } = false;
    public string? SmtpServer { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordEncrypted { get; set; }
    public bool? SmtpUseTls { get; set; } = true;
    public string? SmtpFromEmail { get; set; }
    public string? SmtpHeloHostname { get; set; }
    public bool? SecurityAutoBanEnabled { get; set; } = false;
    public int? Security404Threshold { get; set; } = 100;
    public int? Security404BanDuration { get; set; } = 0;
    public int? SecurityApiThreshold { get; set; } = 100;
    public int? SecurityApiBanDuration { get; set; } = 0;
    public int? SecurityRepeatOffenderLimit { get; set; } = 2;
}
