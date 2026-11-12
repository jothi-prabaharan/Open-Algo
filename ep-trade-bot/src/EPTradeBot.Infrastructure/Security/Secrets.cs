using System.Security.Cryptography;
using System.Text;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure.Security;

/// <summary>
/// Encrypts broker tokens at rest with AES-256-GCM. The key is derived from
/// <c>Security:AppKey</c> by HKDF, and every ciphertext is bound to its account and
/// field as associated data, so a value copied to another row or column fails to
/// decrypt and reads as "not set" instead of authenticating the wrong account.
/// </summary>
public sealed class TokenProtector(IOptions<SecurityOptions> options)
{
    private const byte FormatVersion = 1;
    private readonly byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(options.Value.AppKey), 32,
        salt: Encoding.UTF8.GetBytes("ep-trade-bot/token-protector"), info: Encoding.UTF8.GetBytes("broker-tokens/v1"));

    public string Protect(Guid accountId, string field, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag, Associated(accountId, field));
        return Convert.ToBase64String([FormatVersion, .. nonce, .. tag, .. cipher]);
    }

    /// <summary>The plaintext, or null when the value is absent, damaged, or belongs to another account or field.</summary>
    public string? Unprotect(Guid accountId, string field, string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var raw = Convert.FromBase64String(protectedValue);
            if (raw.Length < 29 || raw[0] != FormatVersion) return null;
            var nonce = raw.AsSpan(1, 12);
            var tag = raw.AsSpan(13, 16);
            var cipher = raw.AsSpan(29);
            var data = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, data, Associated(accountId, field));
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Associated(Guid accountId, string field) => Encoding.UTF8.GetBytes($"{accountId:N}/{field}");
}

/// <summary>Issues and checks /api/v1 keys. A key is 64 hex characters; only its HMAC-SHA256 under the pepper is stored.</summary>
public sealed class ApiKeyService(IDbContextFactory<EpTradeDbContext> contexts, IOptions<SecurityOptions> options)
{
    private readonly byte[] pepper = Encoding.UTF8.GetBytes(options.Value.ApiKeyPepper);

    public string Hash(string apiKey) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(apiKey)));

    /// <summary>Creates a key for the account and returns it. It cannot be read back later.</summary>
    public async Task<(AccountApiKey Record, string Key)> IssueAsync(Guid accountId, string label, CancellationToken cancellationToken = default, string orderMode = "auto")
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var record = new AccountApiKey { AccountId = accountId, Label = label, KeyHash = Hash(key), OrderMode = orderMode };
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.AccountApiKeys.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return (record, key);
    }

    /// <summary>The account a key belongs to, or null for an unknown or revoked key.</summary>
    public async Task<BrokerAccount?> ResolveAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 256) return null;
        var hash = Hash(apiKey);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var accountId = await db.AccountApiKeys.AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RevokedAtUtc == null)
            .Select(k => (Guid?)k.AccountId)
            .SingleOrDefaultAsync(cancellationToken);
        return accountId is null ? null : await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
    }

    /// <summary>The account, the key's id and the key's order mode, or null for an unknown or revoked key.</summary>
    public async Task<(BrokerAccount Account, Guid KeyId, string OrderMode)?> ResolveCallAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 256) return null;
        var hash = Hash(apiKey);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var key = await db.AccountApiKeys.AsNoTracking().Where(k => k.KeyHash == hash && k.RevokedAtUtc == null).SingleOrDefaultAsync(cancellationToken);
        if (key is null) return null;
        var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == key.AccountId, cancellationToken);
        return account is null ? null : (account, key.Id, key.OrderMode);
    }

    /// <summary>
    /// Revokes a key. One conditional update, so two callers revoking the same key at once (a strategy's exit watcher and the platform shutting down) both succeed instead of one failing on the row the other just
    /// changed; a key that is already revoked keeps its first revocation time.
    /// </summary>
    public async Task<bool> RevokeAsync(Guid accountId, Guid keyId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var revoked = await db.AccountApiKeys.Where(k => k.Id == keyId && k.AccountId == accountId && k.RevokedAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAtUtc, now), cancellationToken);
        return revoked > 0 || await db.AccountApiKeys.AnyAsync(k => k.Id == keyId && k.AccountId == accountId, cancellationToken);
    }
}
