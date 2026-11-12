using System.Net;
using System.Text.Json.Nodes;
using EPTradeBot.Api.Security;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Mcp;
using EPTradeBot.Infrastructure.Flow;
using EPTradeBot.Infrastructure.Mcp;
using EPTradeBot.Infrastructure.OpenAlgoApi;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Api.Endpoints;

/// <summary>
/// Remote MCP: the /mcp endpoint, the OAuth server it relies on, and the trader's controls over what has been approved. Port of OpenAlgo's blueprints/mcp_http.py and
/// mcp_oauth.py. The approval page is plain server-rendered HTML with no script, because the trader approves with the admin token typed into it.
/// </summary>
internal static class McpEndpoints
{
    private const int MaxBodyBytes = 1_000_000;

    public static void MapMcp(this WebApplication app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResource);
        app.MapGet("/mcp/.well-known/oauth-protected-resource", ProtectedResource);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServer);
        app.MapPost("/oauth/register", Register);
        app.MapGet("/oauth/authorize", ShowConsent);
        app.MapPost("/oauth/authorize", Decide);
        app.MapPost("/oauth/token", Token);
        app.MapMethods("/mcp", ["OPTIONS"], Preflight);
        app.MapPost("/mcp", Dispatch);
        // The streaming (server-sent events) channel is optional in the protocol; this server answers every request in the response to it and says so.
        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed)).AddEndpointFilter(async (c, n) => { c.HttpContext.Response.Headers.Allow = "POST, OPTIONS"; return await n(c); });
        app.MapDelete("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
    }

    public static RouteGroupBuilder MapMcpManagement(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/mcp").AddEndpointFilter<AdminTokenFilter>();

        admin.MapGet("/clients", async (EpTradeDbContext db, CancellationToken ct) =>
            Results.Ok((await db.McpClients.AsNoTracking().OrderBy(c => c.CreatedAtUtc).ToListAsync(ct)).Select(c => new { client_id = c.ClientId, name = c.Name, redirect_uris = JsonNode.Parse(c.RedirectUrisJson), created_at_utc = c.CreatedAtUtc })));

        admin.MapDelete("/clients/{clientId}", async (string clientId, EpTradeDbContext db, CancellationToken ct) =>
        {
            var removed = await db.McpClients.Where(c => c.ClientId == clientId).ExecuteDeleteAsync(ct);
            if (removed == 0) return Results.Problem(title: "Not found.", detail: "No such application.", statusCode: StatusCodes.Status404NotFound);
            await db.McpTokens.Where(t => t.ClientId == clientId && t.RevokedAtUtc == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAtUtc, DateTime.UtcNow), ct);
            return Results.NoContent();
        });

        admin.MapGet("/tokens", async (EpTradeDbContext db, CancellationToken ct) =>
            Results.Ok((await db.McpTokens.AsNoTracking().OrderByDescending(t => t.CreatedAtUtc).ToListAsync(ct)).Select(t => new
            {
                id = t.Id, client_id = t.ClientId, account_id = t.AccountId, scope = t.Scope, order_mode = t.OrderMode, created_at_utc = t.CreatedAtUtc, last_used_at_utc = t.LastUsedAtUtc,
                revoked = t.RevokedAtUtc != null, access_expires_at_utc = t.AccessExpiresAtUtc, refresh_expires_at_utc = t.RefreshExpiresAtUtc,
            })));

        admin.MapDelete("/tokens/{id:guid}", async (Guid id, EpTradeDbContext db, CancellationToken ct) =>
            await db.McpTokens.Where(t => t.Id == id && t.RevokedAtUtc == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAtUtc, DateTime.UtcNow), ct) == 0
                ? Results.Problem(title: "Not found.", detail: "No such active access.", statusCode: StatusCodes.Status404NotFound)
                : Results.NoContent());

        admin.MapGet("/calls", async (int? limit, EpTradeDbContext db, CancellationToken ct) =>
            Results.Ok((await db.McpCalls.AsNoTracking().OrderByDescending(c => c.Id).Take(Math.Clamp(limit ?? 100, 1, 1000)).ToListAsync(ct))
                .Select(c => new { at_utc = c.AtUtc, client_id = c.ClientId, token_id = c.TokenId, tool = c.Tool, scope = c.Scope, params_hash = c.ParamsHash, duration_ms = c.DurationMs, outcome = c.Outcome })));

        return v1;
    }

    // ---- discovery ---------------------------------------------------------------------------------------

    private static string BaseUrl(HttpContext ctx, McpOptions o) => (o.PublicBaseUrl is { Length: > 0 } b ? b : $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');

    private static IResult ProtectedResource(HttpContext ctx, IOptions<McpOptions> options) => Results.Json(new JsonObject
    {
        ["resource"] = $"{BaseUrl(ctx, options.Value)}/mcp", ["authorization_servers"] = new JsonArray(BaseUrl(ctx, options.Value)),
        ["scopes_supported"] = new JsonArray(McpOAuthService.Scopes.Select(s => (JsonNode?)s).ToArray()), ["bearer_methods_supported"] = new JsonArray("header"),
    });

    private static IResult AuthorizationServer(HttpContext ctx, IOptions<McpOptions> options)
    {
        var b = BaseUrl(ctx, options.Value);
        return Results.Json(new JsonObject
        {
            ["issuer"] = b, ["authorization_endpoint"] = $"{b}/oauth/authorize", ["token_endpoint"] = $"{b}/oauth/token", ["registration_endpoint"] = $"{b}/oauth/register",
            ["response_types_supported"] = new JsonArray("code"), ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"), ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
            ["scopes_supported"] = new JsonArray(McpOAuthService.Scopes.Select(s => (JsonNode?)s).ToArray()),
        });
    }

    // ---- registration ------------------------------------------------------------------------------------

    private static async Task<IResult> Register(HttpRequest request, McpOAuthService oauth, CancellationToken ct)
    {
        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            body = null;
        }

        if (body is null) return OAuthJson(new OAuthError("invalid_client_metadata", "The registration must be a JSON object."));
        var uris = (body["redirect_uris"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList();
        var (client, error) = await oauth.RegisterAsync(body["client_name"]?.GetValue<string>(), uris, ct);
        if (error is not null) return OAuthJson(error);
        return Results.Json(new JsonObject
        {
            ["client_id"] = client!.ClientId, ["client_name"] = client.Name, ["redirect_uris"] = new JsonArray(uris!.Distinct().Select(u => (JsonNode?)u).ToArray()),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"), ["response_types"] = new JsonArray("code"), ["token_endpoint_auth_method"] = "none",
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult OAuthJson(OAuthError e) =>
        Results.Json(new JsonObject { ["error"] = e.Error, ["error_description"] = e.Description }, statusCode: e.Status);

    // ---- the approval page -------------------------------------------------------------------------------

    private sealed record AuthorizeRequest(string ClientId, string RedirectUri, string Scope, string State, string Challenge, string Method, string ResponseType);

    private static AuthorizeRequest ReadAuthorize(IQueryCollection q) =>
        new(q["client_id"].ToString(), q["redirect_uri"].ToString(), q["scope"].ToString(), q["state"].ToString(), q["code_challenge"].ToString(), q["code_challenge_method"].ToString(), q["response_type"].ToString());

    private static AuthorizeRequest ReadAuthorize(IFormCollection f) =>
        new(f["client_id"].ToString(), f["redirect_uri"].ToString(), f["scope"].ToString(), f["state"].ToString(), f["code_challenge"].ToString(), f["code_challenge_method"].ToString(), f["response_type"].ToString());

    private static IResult Page(string title, string bodyHtml, int status = 200, string? formTarget = null)
    {
        var html = $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{WebUtility.HtmlEncode(title)}</title>"
            + "<style>body{font:16px/1.5 system-ui,sans-serif;max-width:34rem;margin:2rem auto;padding:0 1rem;color:#1a1a1a}h1{font-size:1.3rem}label{display:block;margin:.8rem 0 .2rem;font-weight:600}"
            + "select,input[type=password]{width:100%;padding:.5rem;font:inherit}.row{margin:.3rem 0}.actions{margin-top:1.4rem;display:flex;gap:.8rem}button{font:inherit;padding:.6rem 1.2rem;cursor:pointer}"
            + "button.allow{background:#0b5fff;color:#fff;border:0;border-radius:.3rem}.note{color:#555;font-size:.9rem}.warn{color:#9a3b00}</style></head>"
            + $"<body>{bodyHtml}</body></html>";
        return new HtmlResult(html, status, formTarget);
    }

    private sealed class HtmlResult(string html, int status, string? formTarget) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; form-action 'self'{(formTarget is null ? "" : " " + formTarget)}";
            await ctx.Response.WriteAsync(html);
        }
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static IResult Problem(string message) => Page("Cannot continue", $"<h1>This request cannot be approved</h1><p>{Enc(message)}</p><p class=\"note\">Nothing was shared. Close this page and start again from the application.</p>", 400);

    /// <summary>Why the request cannot even be answered by a redirect (an unknown application or address), or null. Such errors are shown here, never sent to an address that was not registered.</summary>
    private static async Task<(McpClient? Client, string? Problem)> CheckClientAsync(AuthorizeRequest r, McpOAuthService oauth, CancellationToken ct)
    {
        var client = await oauth.FindClientAsync(r.ClientId, ct);
        if (client is null) return (null, "This application is not registered with EP Trade bot.");
        if (!McpOAuthService.RedirectMatches(client, r.RedirectUri)) return (null, "The return address does not match the one this application registered.");
        return (client, null);
    }

    private static IResult Redirect(string redirectUri, string state, params (string Key, string Value)[] pairs)
    {
        var sep = redirectUri.Contains('?') ? '&' : '?';
        var all = new List<(string Key, string Value)>(pairs) { ("state", state) };
        var query = string.Join('&', all.Where(p => p.Value.Length > 0).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return Results.Redirect($"{redirectUri}{sep}{query}", permanent: false);
    }

    /// <summary>The scopes asked for, or null when one is not a scope this server has. No scope asked means read.</summary>
    private static List<string>? ParseScopes(string scope)
    {
        var asked = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        if (asked.Count == 0) asked = [McpSession.ReadScope];
        return asked.All(McpOAuthService.Scopes.Contains) ? asked : null;
    }

    private static async Task<IResult> ShowConsent(HttpRequest request, McpOAuthService oauth, EpTradeDbContext db, CancellationToken ct)
    {
        var r = ReadAuthorize(request.Query);
        var (client, problem) = await CheckClientAsync(r, oauth, ct);
        if (client is null) return Problem(problem!);
        if (r.ResponseType != "code") return Redirect(r.RedirectUri, r.State, ("error", "unsupported_response_type"));
        if (r.Challenge.Length is < 43 or > 128 || r.Method != "S256") return Redirect(r.RedirectUri, r.State, ("error", "invalid_request"), ("error_description", "PKCE with S256 is required."));
        if (ParseScopes(r.Scope) is not { } scopes) return Redirect(r.RedirectUri, r.State, ("error", "invalid_scope"));

        var accounts = await db.BrokerAccounts.AsNoTracking().Where(a => a.IsEnabled).OrderBy(a => a.DisplayName).ToListAsync(ct);
        var wantsWrite = scopes.Contains(McpSession.WriteScope);
        var options = string.Concat(accounts.Select(a => $"<option value=\"{a.Id}\">{Enc(a.DisplayName)} ({(a.AccountType == EPTradeBot.Core.Entities.Core.AccountType.PAPER ? "sandbox" : "live")})</option>"));
        var hidden = string.Concat(new[] { ("client_id", r.ClientId), ("redirect_uri", r.RedirectUri), ("scope", string.Join(' ', scopes)), ("state", r.State), ("code_challenge", r.Challenge), ("code_challenge_method", r.Method), ("response_type", "code") }
            .Select(p => $"<input type=\"hidden\" name=\"{p.Item1}\" value=\"{Enc(p.Item2)}\">"));
        var writeBox = wantsWrite
            ? "<div class=\"row\"><label><input type=\"checkbox\" name=\"allow_write\" value=\"1\" checked> Let it place and change orders</label></div>"
            : "<p class=\"note\">It asked only to read.</p>";
        var body = $"<h1>Allow {Enc(client.Name)} to use EP Trade bot?</h1>"
            + "<p>It will be able to read your account and the market"
            + (wantsWrite ? ", and to place and change orders if you allow that below" : "") + ".</p>"
            + (accounts.Count == 0 ? "<p class=\"warn\">There is no account to share yet. Add one first.</p>" : "")
            + $"<form method=\"post\" action=\"/oauth/authorize\">{hidden}"
            + $"<label for=\"account\">Which account</label><select id=\"account\" name=\"account_id\" required>{options}</select>"
            + writeBox
            + (wantsWrite
                ? "<label>Its orders</label><div class=\"row\"><label><input type=\"radio\" name=\"order_mode\" value=\"semi_auto\" checked> Wait in the Action Center until I approve them (recommended)</label></div>"
                  + "<div class=\"row\"><label><input type=\"radio\" name=\"order_mode\" value=\"auto\"> Send straight away, after the usual risk checks</label></div>"
                : "")
            + "<label for=\"token\">Your admin token</label><input id=\"token\" name=\"admin_token\" type=\"password\" autocomplete=\"off\" required>"
            + "<p class=\"note\">Only you can approve this. You can end the access at any time from your settings.</p>"
            + "<div class=\"actions\"><button class=\"allow\" name=\"decision\" value=\"allow\">Allow</button><button name=\"decision\" value=\"deny\" formnovalidate>Do not allow</button></div></form>";
        return Page("Allow access", body, formTarget: new Uri(r.RedirectUri).GetLeftPart(UriPartial.Authority));
    }

    private static async Task<IResult> Decide(HttpRequest request, McpOAuthService oauth, AdminTokenCheck admin, EpTradeDbContext db, CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var r = ReadAuthorize(form);
        var (client, problem) = await CheckClientAsync(r, oauth, ct);
        if (client is null) return Problem(problem!);
        if (form["decision"] != "allow") return Redirect(r.RedirectUri, r.State, ("error", "access_denied"));
        if (r.Challenge.Length is < 43 or > 128 || r.Method != "S256" || r.ResponseType != "code") return Redirect(r.RedirectUri, r.State, ("error", "invalid_request"));
        if (ParseScopes(r.Scope) is not { } scopes) return Redirect(r.RedirectUri, r.State, ("error", "invalid_scope"));
        if (!admin.Matches(form["admin_token"].ToString())) return Problem("The admin token was not right. Go back and try again.");
        if (!Guid.TryParse(form["account_id"], out var accountId) || !await db.BrokerAccounts.AnyAsync(a => a.Id == accountId && a.IsEnabled, ct)) return Problem("Choose one of your accounts.");

        // What is granted is what the trader ticked, which may be less than what was asked for: write only if they allowed it, and semi-auto unless they chose otherwise.
        var granted = scopes.Where(s => s == McpSession.ReadScope || form["allow_write"] == "1").ToList();
        if (granted.Count == 0) granted = [McpSession.ReadScope];
        var mode = form["order_mode"] == "auto" ? "auto" : "semi_auto";
        var code = await oauth.ApproveAsync(client, r.RedirectUri, string.Join(' ', granted), accountId, mode, r.Challenge, ct);
        return Redirect(r.RedirectUri, r.State, ("code", code));
    }

    // ---- tokens ------------------------------------------------------------------------------------------

    private static async Task<IResult> Token(HttpRequest request, McpOAuthService oauth, CancellationToken ct)
    {
        if (!request.HasFormContentType) return OAuthJson(new OAuthError("invalid_request", "Send the request as a form (application/x-www-form-urlencoded)."));
        var f = await request.ReadFormAsync(ct);
        (IssuedTokens? Tokens, OAuthError? Error) result = f["grant_type"].ToString() switch
        {
            "authorization_code" => await oauth.ExchangeCodeAsync(f["client_id"], f["code"], f["redirect_uri"], f["code_verifier"], ct),
            "refresh_token" => await oauth.RefreshAsync(f["client_id"], f["refresh_token"], ct),
            _ => (null, new OAuthError("unsupported_grant_type", "Use authorization_code or refresh_token.")),
        };
        if (result.Error is not null) return OAuthJson(result.Error);
        var t = result.Tokens!;
        return new NoStoreJson(new JsonObject { ["access_token"] = t.AccessToken, ["token_type"] = "Bearer", ["expires_in"] = t.ExpiresInSeconds, ["refresh_token"] = t.RefreshToken, ["scope"] = t.Scope });
    }

    private sealed class NoStoreJson(JsonObject body) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(body.ToJsonString());
        }
    }

    // ---- /mcp --------------------------------------------------------------------------------------------

    /// <summary>Whether a browser origin may call /mcp. A request with no origin (a desktop client, a script) is not a browser and is not affected.</summary>
    private static bool OriginAllowed(HttpContext ctx, McpOptions o)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        return origin.Length == 0 || o.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    private static void Cors(HttpContext ctx, McpOptions o)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || !OriginAllowed(ctx, o)) return;
        ctx.Response.Headers.AccessControlAllowOrigin = origin;
        ctx.Response.Headers.Vary = "Origin";
        ctx.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, Mcp-Protocol-Version, Mcp-Session-Id";
        ctx.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
        ctx.Response.Headers.AccessControlExposeHeaders = "WWW-Authenticate";
    }

    private static IResult Preflight(HttpContext ctx, IOptions<McpOptions> options)
    {
        if (!OriginAllowed(ctx, options.Value)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        Cors(ctx, options.Value);
        return Results.NoContent();
    }

    private static async Task<IResult> Dispatch(HttpContext ctx, McpOAuthService oauth, McpServer server, OpenAlgoApiService api, IOptions<McpOptions> options, CancellationToken ct)
    {
        var o = options.Value;
        if (!OriginAllowed(ctx, o)) return Results.Json(new { error = "origin_not_allowed", error_description = "This browser origin is not allowed to use this server." }, statusCode: StatusCodes.Status403Forbidden);
        Cors(ctx, o);

        var header = ctx.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var metadata = $"resource_metadata=\"{BaseUrl(ctx, o)}/.well-known/oauth-protected-resource\"";
        if (bearer is null)
        {
            ctx.Response.Headers.WWWAuthenticate = $"Bearer {metadata}";
            return Results.Json(new { error = "unauthorized", error_description = "Sign in to use this server." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var caller = await oauth.AuthenticateAsync(bearer, ct);
        if (caller is null)
        {
            ctx.Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", {metadata}";
            return Results.Json(new { error = "invalid_token", error_description = "This access has expired or been ended." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (ctx.Request.ContentLength > MaxBodyBytes) return Results.Json(McpServer.Error(null, -32600, "The request is too large."), statusCode: StatusCodes.Status413PayloadTooLarge);
        JsonNode? message;
        try
        {
            message = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Json(McpServer.ParseFailure(), statusCode: StatusCodes.Status400BadRequest);
        }

        var session = new McpSession
        {
            Api = new FlowApi(api, caller.Account, caller.Token.OrderMode), ClientId = caller.Token.ClientId, TokenId = caller.Token.Id.ToString("N"),
            Scopes = caller.Token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal), UtcNow = () => DateTime.UtcNow,
        };
        var outcome = await server.HandleAsync(message, session, ct);
        if (outcome.ScopeError is not null)
            ctx.Response.Headers.WWWAuthenticate = $"Bearer error=\"{outcome.ScopeError}\", scope=\"{McpSession.WriteScope}\", {metadata}";
        if (outcome.Response is null) return Results.Accepted();
        return Results.Json(outcome.Response, statusCode: outcome.HttpStatus);
    }
}
