using EPTradeBot.Api.Security;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Contracts.Management;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Infrastructure.Agent;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Api.Endpoints;

/// <summary>The assistant: which language model it uses, and the conversation. Admin-token calls.</summary>
internal static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgent(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/agent").AddEndpointFilter<AdminTokenFilter>().WithDataAnnotations();

        admin.MapGet("/provider", async (EpTradeDbContext db, CancellationToken ct) =>
            await db.AgentSettings.AsNoTracking().SingleOrDefaultAsync(ct) is { } s
                ? Results.Ok(new { status = "success", endpoint = s.Endpoint, model = s.Model, has_key = !string.IsNullOrEmpty(s.ProtectedApiKey) })
                : Results.Ok(new { status = "success", endpoint = (string?)null, model = (string?)null, has_key = false }));

        admin.MapPut("/provider", async (SetAgentProviderRequest r, EpTradeDbContext db, TokenProtector protector, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                return Results.Problem(title: "Invalid address.", detail: "The service address must start with https:// (http:// is accepted only for a service on this computer).", statusCode: StatusCodes.Status400BadRequest);
            var s = await db.AgentSettings.SingleOrDefaultAsync(ct);
            if (s is null) db.AgentSettings.Add(s = new AgentSetting());
            s.Endpoint = r.Endpoint;
            s.Model = r.Model;
            if (!string.IsNullOrWhiteSpace(r.ApiKey)) s.ProtectedApiKey = protector.Protect(Guid.Empty, OpenAiCompatibleClientFactory.KeyField, r.ApiKey.Trim());
            s.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "success", endpoint = s.Endpoint, model = s.Model, has_key = !string.IsNullOrEmpty(s.ProtectedApiKey) });
        });

        admin.MapPost("/chat", async (AgentChatRequest r, AgentService agent, CancellationToken ct) =>
        {
            if (r.Messages[^1].Role != "user") return Results.Problem(title: "Nothing to answer.", detail: "The last message must be the trader's.", statusCode: StatusCodes.Status400BadRequest);
            var reply = await agent.ChatAsync(r.AccountId!.Value, r.Messages, ct);
            return Results.Json(new
            {
                status = reply.Ok ? "success" : "error",
                message = reply.Text,
                tool_calls = reply.ToolCalls.Select(c => new { name = c.Name, arguments = c.Arguments, ok = c.Ok, summary = c.Summary }),
                proposals = reply.Proposals,
            }, statusCode: reply.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        return v1;
    }
}
