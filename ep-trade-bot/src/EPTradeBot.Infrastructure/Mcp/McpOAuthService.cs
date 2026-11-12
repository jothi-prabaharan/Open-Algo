using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Mcp;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure.Mcp;

public sealed record OAuthError(string Error, string Description, int Status = 400);

public sealed record IssuedTokens(string AccessToken, string RefreshToken, int ExpiresInSeconds, string Scope);

/// <summary>The caller an access token stands for.</summary>
public sealed record McpCaller(McpToken Token, BrokerAccount Account);

/// <summary>
/// The authorisation server for remote MCP clients: OAuth 2.1 authorisation code with PKCE for public clients, as MCP clients use. Nothing is granted until the trader
/// approves it with the admin token; the approval names the account, the scopes and whether the client's orders are sent or wait for approval. Codes and tokens are kept
/// only as hashes, a code works once, a refresh token is replaced each time it is used, and a replaced refresh token coming back revokes the whole grant.
/// </summary>
public sealed class McpOAuthService(IDbContextFactory<EpTradeDbContext> contexts, ApiKeyService hasher, IOptions<McpOptions> options, TimeProvider clock)
{
    public static readonly IReadOnlyList<string> Scopes = [McpSession.ReadScope, McpSession.WriteScope];

    private McpOptions Options => options.Value;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static string NewSecret(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    // ---- clients -----------------------------------------------------------------------------------------

    /// <summary>A redirect address a client may register: https, or http on this computer (for a desktop client's own listener). No fragment, no credentials.</summary>
    public static bool IsAllowedRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || uri.Length > 500 || u.Fragment.Length > 0 || u.UserInfo.Length > 0) return false;
        return u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
    }

    public async Task<(McpClient? Client, OAuthError? Error)> RegisterAsync(string? name, IReadOnlyList<string>? redirectUris, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return (null, new OAuthError("invalid_client_metadata", "The application needs a name of up to 100 characters."));
        if (redirectUris is null || redirectUris.Count is 0 or > 10 || redirectUris.Any(u => !IsAllowedRedirect(u)))
            return (null, new OAuthError("invalid_redirect_uri", "Give one to ten redirect addresses, each starting with https:// (http:// only for this computer), with no # part."));
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.McpClients.CountAsync(ct) >= Options.MaxClients) return (null, new OAuthError("invalid_client_metadata", "This install has as many registered applications as it allows. The trader can remove old ones.", 429));
        var client = new McpClient { ClientId = NewSecret("mcpc_")[..40], Name = name.Trim(), RedirectUrisJson = JsonSerializer.Serialize(redirectUris.Distinct()) };
        db.McpClients.Add(client);
        await db.SaveChangesAsync(ct);
        return (client, null);
    }

    public async Task<McpClient?> FindClientAsync(string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 64) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.McpClients.AsNoTracking().SingleOrDefaultAsync(c => c.ClientId == clientId, ct);
    }

    public static bool RedirectMatches(McpClient client, string? redirectUri) =>
        redirectUri is not null && (JsonSerializer.Deserialize<List<string>>(client.RedirectUrisJson) ?? []).Contains(redirectUri, StringComparer.Ordinal);

    // ---- approval ----------------------------------------------------------------------------------------

    /// <summary>Records the trader's approval and returns the one-time code to hand back to the client.</summary>
    public async Task<string> ApproveAsync(McpClient client, string redirectUri, string scope, Guid accountId, string orderMode, string challenge, CancellationToken ct)
    {
        var code = NewSecret("mcpa_");
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.McpAuthorizationCodes.Add(new McpAuthorizationCode
        {
            CodeHash = hasher.Hash(code), ClientId = client.ClientId, AccountId = accountId, Scope = scope, OrderMode = orderMode, RedirectUri = redirectUri,
            CodeChallenge = challenge, ExpiresAtUtc = Now.AddMinutes(10),
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    // ---- tokens ------------------------------------------------------------------------------------------

    public static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<(IssuedTokens? Tokens, OAuthError? Error)> ExchangeCodeAsync(string? clientId, string? code, string? redirectUri, string? verifier, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier) || verifier.Length is < 43 or > 128)
            return (null, new OAuthError("invalid_request", "A code and a PKCE code verifier of 43 to 128 characters are required."));
        var hash = hasher.Hash(code);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = Now;
        // Claimed before it is checked: the first caller to present a code wins it, and a second finds it used, even when two arrive together.
        var claimed = await db.McpAuthorizationCodes.Where(c => c.CodeHash == hash && c.UsedAtUtc == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAtUtc, now), ct);
        var row = await db.McpAuthorizationCodes.AsNoTracking().SingleOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (claimed == 0 || row is null) return (null, new OAuthError("invalid_grant", "That code was not issued, or has already been used."));
        if (row.ClientId != clientId || row.RedirectUri != redirectUri || row.ExpiresAtUtc < now)
            return (null, new OAuthError("invalid_grant", "That code does not belong to this application and address, or has expired."));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Challenge(verifier)), Encoding.ASCII.GetBytes(row.CodeChallenge)))
            return (null, new OAuthError("invalid_grant", "The PKCE code verifier does not match."));

        var access = NewSecret("mcp_");
        var refresh = NewSecret("mcr_");
        db.McpTokens.Add(new McpToken
        {
            ClientId = row.ClientId, AccountId = row.AccountId, Scope = row.Scope, OrderMode = row.OrderMode, AccessHash = hasher.Hash(access), RefreshHash = hasher.Hash(refresh),
            AccessExpiresAtUtc = now.AddMinutes(Options.AccessTokenMinutes), RefreshExpiresAtUtc = now.AddDays(Options.RefreshTokenDays), CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(ct);
        return (new IssuedTokens(access, refresh, Options.AccessTokenMinutes * 60, row.Scope), null);
    }

    public async Task<(IssuedTokens? Tokens, OAuthError? Error)> RefreshAsync(string? clientId, string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) return (null, new OAuthError("invalid_request", "A refresh token is required."));
        var hash = hasher.Hash(refreshToken);
        var now = Now;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var token = await db.McpTokens.SingleOrDefaultAsync(t => t.RefreshHash == hash || t.PreviousRefreshHash == hash, ct);
        if (token is null || token.ClientId != clientId) return (null, new OAuthError("invalid_grant", "That refresh token is not valid."));
        if (token.PreviousRefreshHash == hash && token.RefreshHash != hash)
        {
            // An old refresh token coming back means a copy of it was kept. The grant ends, for whoever holds it.
            token.RevokedAtUtc ??= now;
            await db.SaveChangesAsync(ct);
            return (null, new OAuthError("invalid_grant", "That refresh token was already used. Access was ended for safety; approve the application again."));
        }

        if (token.RevokedAtUtc is not null || token.RefreshExpiresAtUtc < now) return (null, new OAuthError("invalid_grant", "That access has ended. Approve the application again."));

        var access = NewSecret("mcp_");
        var refresh = NewSecret("mcr_");
        token.PreviousRefreshHash = token.RefreshHash;
        token.AccessHash = hasher.Hash(access);
        token.RefreshHash = hasher.Hash(refresh);
        token.AccessExpiresAtUtc = now.AddMinutes(Options.AccessTokenMinutes);
        token.RefreshExpiresAtUtc = now.AddDays(Options.RefreshTokenDays);
        await db.SaveChangesAsync(ct);
        return (new IssuedTokens(access, refresh, Options.AccessTokenMinutes * 60, token.Scope), null);
    }

    /// <summary>The caller an access token stands for, or null when it is unknown, expired, revoked, or its account is switched off.</summary>
    public async Task<McpCaller?> AuthenticateAsync(string? accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken) || accessToken.Length > 200) return null;
        var hash = hasher.Hash(accessToken);
        var now = Now;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var token = await db.McpTokens.AsNoTracking().SingleOrDefaultAsync(t => t.AccessHash == hash, ct);
        if (token is null || token.RevokedAtUtc is not null || token.AccessExpiresAtUtc < now) return null;
        var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == token.AccountId, ct);
        if (account is null || !account.IsEnabled) return null;
        if (token.LastUsedAtUtc is null || now - token.LastUsedAtUtc > TimeSpan.FromMinutes(1))
            await db.McpTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.LastUsedAtUtc, now), ct);
        return new McpCaller(token, account);
    }
}
