using EPTradeBot.Api.Security;
using EPTradeBot.Api.Validation;
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
}
