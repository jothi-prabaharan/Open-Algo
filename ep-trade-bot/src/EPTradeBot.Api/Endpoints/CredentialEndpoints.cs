using EPTradeBot.Api.Security;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Contracts.Management;
using EPTradeBot.Infrastructure.Brokers;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Api.Endpoints;

/// <summary>Per-account /api/v1 keys and the broker session of a LIVE account (admin token until login arrives).</summary>
internal static class CredentialEndpoints
{
    public static RouteGroupBuilder MapCredentials(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("").AddEndpointFilter<AdminTokenFilter>().WithDataAnnotations();

        admin.MapPost("/accounts/{accountId:guid}/apikeys", async (Guid accountId, CreateApiKeyRequest request, EpTradeDbContext db, ApiKeyService keys, CancellationToken ct) =>
        {
            if (!await db.BrokerAccounts.AnyAsync(a => a.Id == accountId, ct)) return AccountEndpoints.NotFound();
            var (record, key) = await keys.IssueAsync(accountId, request.Label.Trim(), ct);
            // The key is shown once. Only its keyed hash is stored.
            return Results.Created($"/api/v1/accounts/{accountId}/apikeys/{record.Id}", new { id = record.Id, label = record.Label, apikey = key, created_at_utc = record.CreatedAtUtc });
        });

        admin.MapGet("/accounts/{accountId:guid}/apikeys", async (Guid accountId, EpTradeDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AccountApiKeys.AsNoTracking().Where(k => k.AccountId == accountId).OrderBy(k => k.CreatedAtUtc)
                .Select(k => new { id = k.Id, label = k.Label, created_at_utc = k.CreatedAtUtc, revoked_at_utc = k.RevokedAtUtc }).ToListAsync(ct)));

        admin.MapDelete("/accounts/{accountId:guid}/apikeys/{keyId:guid}", async (Guid accountId, Guid keyId, ApiKeyService keys, CancellationToken ct) =>
            await keys.RevokeAsync(accountId, keyId, ct) ? Results.NoContent() : AccountEndpoints.NotFound("No such key for this account."));

        admin.MapPut("/accounts/{accountId:guid}/broker-session", async (Guid accountId, SetBrokerSessionRequest request, EpTradeDbContext db, TokenProtector protector, CancellationToken ct) =>
        {
            var account = await db.BrokerAccounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null) return AccountEndpoints.NotFound();
            account.AuthTokenEncrypted = protector.Protect(account.Id, BrokerSessionFactory.AuthTokenField, request.AuthToken);
            account.FeedTokenEncrypted = string.IsNullOrEmpty(request.FeedToken) ? null : protector.Protect(account.Id, BrokerSessionFactory.FeedTokenField, request.FeedToken);
            account.TokenIssuedAtUtc = DateTime.UtcNow;
            account.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Signing in to the account's broker. Start says how (a page to open, or details to ask for); complete exchanges what came back for tokens.
        admin.MapPost("/accounts/{accountId:guid}/broker-login/start", async (Guid accountId, EpTradeDbContext db, IBrokerRegistry registry, BrokerSessionFactory sessions, LoginStates states, CancellationToken ct) =>
        {
            var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null) return AccountEndpoints.NotFound();
            if (LoginRefusal(account, registry, out var login) is { } refused) return refused;
            var state = states.Issue(account.Id);
            if (login!.LoginMethod == LoginMethod.Credentials) return Results.Json(new { status = "success", method = "credentials", fields = login.LoginFields });
            if (login is IBrokerLoginStart asks)
            {
                var start = await asks.StartAsync(sessions.KeysFor(account.Broker), account.ClientId, state, ct);
                return start.Url is { } page ? Results.Json(new { status = "success", method = "redirect", login_url = page, state }) : Failed(start.HttpStatus, start.Message);
            }

            return login.LoginUrl(sessions.KeysFor(account.Broker), state) is { } url
                ? Results.Json(new { status = "success", method = "redirect", login_url = url, state })
                : Failed(409, $"The {login.DisplayName} API key is not set on this server. Add it to the server's settings, then sign in again.");
        });

        admin.MapPost("/accounts/{accountId:guid}/broker-login/complete", async (Guid accountId, BrokerLoginCompleteRequest request, EpTradeDbContext db, IBrokerRegistry registry, BrokerSessionFactory sessions,
            LoginStates states, TokenProtector protector, CancellationToken ct) =>
        {
            var account = await db.BrokerAccounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null) return AccountEndpoints.NotFound();
            if (LoginRefusal(account, registry, out var login) is { } refused) return refused;
            // A sign-in finishes only if this server started it for this account: the answer must carry the state the start gave.
            if (login!.LoginMethod == LoginMethod.Redirect && !states.Consume(account.Id, request.State))
                return Failed(400, "This sign-in was not started from EP Trade bot, or it took too long. Start the sign-in again.");

            var input = new Dictionary<string, string>(StringComparer.Ordinal);
            if (request.RequestToken is not null) input["request_token"] = request.RequestToken;
            if (request.Code is not null) input["code"] = request.Code;
            if (!string.IsNullOrWhiteSpace(account.ClientId)) input["client_id"] = account.ClientId;
            if (request.ClientCode is not null) input["clientcode"] = request.ClientCode;
            if (request.Password is not null) input["password"] = request.Password;
            if (request.Totp is not null) input["totp"] = request.Totp;

            var result = await login.LoginAsync(sessions.KeysFor(account.Broker), input, ct);
            if (!result.Ok) return Failed(result.HttpStatus, result.Message);
            account.AuthTokenEncrypted = protector.Protect(account.Id, BrokerSessionFactory.AuthTokenField, result.AuthToken);
            account.FeedTokenEncrypted = string.IsNullOrEmpty(result.FeedToken) ? null : protector.Protect(account.Id, BrokerSessionFactory.FeedTokenField, result.FeedToken);
            account.TokenIssuedAtUtc = DateTime.UtcNow;
            account.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Json(new { status = "success" });
        });

        admin.MapDelete("/accounts/{accountId:guid}/broker-session", async (Guid accountId, EpTradeDbContext db, CancellationToken ct) =>
        {
            var account = await db.BrokerAccounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null) return AccountEndpoints.NotFound();
            account.AuthTokenEncrypted = null;
            account.FeedTokenEncrypted = null;
            account.TokenIssuedAtUtc = null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return v1;
    }

    private static IResult Failed(int status, string message) => Results.Json(new { status = "error", message }, statusCode: status);

    /// <summary>Why this account cannot sign in through EP Trade bot, or null with the broker's login in <paramref name="login"/>.</summary>
    private static IResult? LoginRefusal(BrokerAccount account, IBrokerRegistry registry, out IBrokerLogin? login)
    {
        login = null;
        if (account.AccountType != AccountType.LIVE) return Failed(400, $"{account.DisplayName} is a sandbox account and has no broker to sign in to.");
        if (registry.Find(account.Broker) is null) return Failed(404, $"{account.Broker} is not enabled on this server.");
        login = registry.Get<IBrokerLogin>(account.Broker);
        return login is null ? Failed(501, $"Signing in to {account.Broker} from EP Trade bot is not available yet.") : null;
    }
}
