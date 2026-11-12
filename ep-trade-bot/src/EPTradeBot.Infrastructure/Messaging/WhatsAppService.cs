using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EPTradeBot.Infrastructure.Messaging;

/// <summary>What a paired WhatsApp device does. EP Trade bot ships none: a phone has to be paired through a WhatsApp Web session, which this platform does not run yet.</summary>
public interface IWhatsAppTransport
{
    bool IsReady { get; }

    /// <summary>The reason the device is not usable when the transport knows it; null otherwise.</summary>
    string? UnavailableReason { get; }

    /// <summary>Sends to each recipient (an empty list means the paired device's own number) and says who got it.</summary>
    Task<WhatsAppReport> SendAsync(IReadOnlyList<string> recipients, string? text, string? imagePath, string? documentPath, string? caption, string? filename, CancellationToken ct);
}

public sealed record WhatsAppReport(IReadOnlyList<string> Sent, IReadOnlyList<(string Recipient, string Error)> Failed);

/// <summary>The default: no device is paired, so every send is refused with the instruction to pair first.</summary>
public sealed class NoWhatsAppDevice : IWhatsAppTransport
{
    public bool IsReady => false;

    public string? UnavailableReason => null;

    public Task<WhatsAppReport> SendAsync(IReadOnlyList<string> recipients, string? text, string? imagePath, string? documentPath, string? caption, string? filename, CancellationToken ct) =>
        Task.FromResult(new WhatsAppReport([], [(string.Join(",", recipients), "No WhatsApp device is paired.")]));
}

/// <summary>Phone numbers, links to people and attachment paths for the WhatsApp send route.</summary>
public sealed partial class WhatsAppService(IDbContextFactory<EpTradeDbContext> contexts, IWhatsAppTransport transport, IConfiguration configuration)
{
    public IWhatsAppTransport Transport => transport;

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();

    private static readonly string[] DeniedRoots = ["/etc", "/proc", "/sys", "/root", "/boot", "/dev", "/var/lib", "/usr", "/bin", "/sbin", "c:\\windows", "c:\\program files"];

    /// <summary>Bare E.164 digits, or empty when the input cannot be a phone number (7 to 15 digits; a fractional number is refused rather than mangled).</summary>
    public static string NormalisePhone(JsonNode? raw)
    {
        if (raw is not JsonValue v) return string.Empty;
        string text;
        if (v.TryGetValue<string>(out var s)) text = s;
        else if (v.TryGetValue<long>(out var l)) text = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if (v.TryGetValue<int>(out var i)) text = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else return string.Empty;
        var digits = NonDigits().Replace(text, "");
        return digits.Length is >= 7 and <= 15 ? digits : string.Empty;
    }

    public static string PhoneToJid(string digits) => $"{digits}@s.whatsapp.net";

    /// <summary>The resolved path of an attachment that is safe to read: absolute, no traversal, outside the system trees, inside an allowed folder, and a real file.</summary>
    public string? ValidateAttachmentPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (path.Replace('\\', '/').Split('/').Contains("..")) return null;
            if (!Path.IsPathRooted(path)) return null;
            var resolved = Path.GetFullPath(path);
            if (DeniedRoots.Any(d => resolved.StartsWith(d, StringComparison.OrdinalIgnoreCase))) return null;
            var configured = configuration["WhatsApp:AttachmentRoots"];
            var roots = string.IsNullOrWhiteSpace(configured) ? [Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "db", "attachments"))] : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();
            if (!roots.Any(r => resolved.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || resolved.Equals(r, StringComparison.OrdinalIgnoreCase))) return null;
            return File.Exists(resolved) ? resolved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task<string?> JidForUsernameAsync(string username, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Set<WhatsappUser>().AsNoTracking().Where(u => u.OpenalgoUsername == username && u.IsActive == true).Select(u => u.WhatsappJid).FirstOrDefaultAsync(ct);
    }

    /// <summary>Whether this username is the operator who paired the device (the record kept at pairing, not the list of people who have messaged the bot).</summary>
    public async Task<bool> IsPairedOwnerAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var owner = (await db.Set<WhatsappConfig>().AsNoTracking().Where(c => c.Id == 1).Select(c => c.OwnerUsername).FirstOrDefaultAsync(ct) ?? "").Trim();
        return owner.Length > 0 && string.Equals(owner, username.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
