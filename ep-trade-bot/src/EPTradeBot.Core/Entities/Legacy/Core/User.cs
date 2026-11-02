// Generated from OpenAlgo table 'users' (database.user_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>users</c>.</summary>
public sealed class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string TotpSecret { get; set; } = "";
    public bool? IsAdmin { get; set; } = false;
    public bool TotpEnabled { get; set; } = false;
    public bool TotpRequiredForLogin { get; set; } = false;
    public bool TotpRequiredForMcp { get; set; } = false;
    public bool TotpRequiredForPasswordReset { get; set; } = false;
}
