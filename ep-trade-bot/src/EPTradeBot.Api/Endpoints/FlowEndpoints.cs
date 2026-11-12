using System.Text.Json.Nodes;
using EPTradeBot.Api.Security;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Contracts.Management;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Flow;
using EPTradeBot.Infrastructure.Flow;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Api.Endpoints;

/// <summary>Flow workflows: build, import and export, switch on and off, run, and read their runs. Admin-token calls, plus the webhook a workflow's trigger listens on.</summary>
internal static class FlowEndpoints
{
    public static RouteGroupBuilder MapFlows(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/flows").AddEndpointFilter<AdminTokenFilter>().WithDataAnnotations();

        admin.MapGet("", async (EpTradeDbContext db, CancellationToken ct) =>
            Results.Ok((await db.FlowDefinitions.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct)).Select(f => View(f, withGraph: false))));

        admin.MapGet("/{id:guid}", async (Guid id, EpTradeDbContext db, CancellationToken ct) =>
            await db.FlowDefinitions.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) is { } f ? Results.Ok(View(f, withGraph: true)) : Missing());

        // The file a workflow is shared as: the same fields an import takes, with nothing that belongs to this install (account, token).
        admin.MapGet("/{id:guid}/export", async (Guid id, EpTradeDbContext db, CancellationToken ct) =>
            await db.FlowDefinitions.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) is { } f
                ? Results.Ok(new JsonObject { ["name"] = f.Name, ["description"] = f.Description, ["nodes"] = JsonNode.Parse(f.NodesJson), ["edges"] = JsonNode.Parse(f.EdgesJson) })
                : Missing());

        admin.MapPost("", async (CreateFlowRequest r, EpTradeDbContext db, CancellationToken ct) =>
        {
            var nodes = (r.Nodes ?? []).ToJsonString();
            var edges = (r.Edges ?? []).ToJsonString();
            if (FlowService.Validate(r.Name, nodes, edges, strict: false) is { Count: > 0 } bad) return Invalid(bad);
            if (Parse(r, out var time, out var days) is { } problem) return problem;
            if (r.AccountId is { } account && !await db.BrokerAccounts.AnyAsync(a => a.Id == account, ct)) return NoAccount();
            if (await db.FlowDefinitions.AnyAsync(f => f.Name == r.Name, ct))
                return Results.Problem(title: "Already exists.", detail: "A workflow with this name already exists.", statusCode: StatusCodes.Status409Conflict);

            var f = new FlowDefinition
            {
                Name = r.Name, Description = r.Description, NodesJson = nodes, EdgesJson = edges, AccountId = r.AccountId,
                ExecutionMode = r.ExecutionMode == "auto" ? ExecutionMode.Auto : ExecutionMode.SemiAuto,
                Exchange = (r.Exchange ?? "NSE").ToUpperInvariant(), IsScheduled = r.IsScheduled ?? false, ScheduleTimeIst = time, ScheduleDays = days,
            };
            db.FlowDefinitions.Add(f);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/flows/{f.Id}", View(f, withGraph: true));
        });

        admin.MapPut("/{id:guid}", async (Guid id, UpdateFlowRequest r, EpTradeDbContext db, FlowTriggers triggers, CancellationToken ct) =>
        {
            var f = await db.FlowDefinitions.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Missing();
            var nodes = r.Nodes is null ? f.NodesJson : r.Nodes.ToJsonString();
            var edges = r.Edges is null ? f.EdgesJson : r.Edges.ToJsonString();
            var name = r.Name ?? f.Name;
            if (FlowService.Validate(name, nodes, edges, strict: false) is { Count: > 0 } bad) return Invalid(bad);
            if (Parse(r, out var time, out var days) is { } problem) return problem;
            if (r.AccountId is { } account && !await db.BrokerAccounts.AnyAsync(a => a.Id == account, ct)) return NoAccount();
            if (r.Name is not null && r.Name != f.Name && await db.FlowDefinitions.AnyAsync(x => x.Name == r.Name, ct))
                return Results.Problem(title: "Already exists.", detail: "A workflow with this name already exists.", statusCode: StatusCodes.Status409Conflict);

            f.Name = name;
            if (r.Description is not null) f.Description = r.Description;
            f.NodesJson = nodes;
            f.EdgesJson = edges;
            if (r.AccountId is not null) f.AccountId = r.AccountId;
            if (r.ExecutionMode is not null) f.ExecutionMode = r.ExecutionMode == "auto" ? ExecutionMode.Auto : ExecutionMode.SemiAuto;
            if (r.Exchange is not null) f.Exchange = r.Exchange.ToUpperInvariant();
            if (r.IsScheduled is { } scheduled) f.IsScheduled = scheduled;
            if (r.ScheduleTime is not null) f.ScheduleTimeIst = time;
            if (r.ScheduleDays is not null) f.ScheduleDays = days;
            f.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // A live workflow keeps running on what it was changed to, not on what it was.
            var warning = f.IsActive ? await triggers.SyncAsync(f.Id, ct) : null;
            return Results.Ok(View(f, withGraph: true, warning));
        });

        admin.MapDelete("/{id:guid}", async (Guid id, EpTradeDbContext db, FlowTriggers triggers, ScheduleRegistry registry, CancellationToken ct) =>
        {
            var f = await db.FlowDefinitions.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Missing();
            db.FlowDefinitions.Remove(f);
            await db.SaveChangesAsync(ct);
            await triggers.SyncAsync(id, ct);
            await registry.RemoveFlowAsync(id, ct);
            return Results.NoContent();
        });

        admin.MapPost("/{id:guid}/activate", async (Guid id, EpTradeDbContext db, FlowTriggers triggers, CancellationToken ct) =>
        {
            var f = await db.FlowDefinitions.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Missing();
            // A workflow is switched on only when it is finished: a half-built graph can be saved but never goes live.
            if (FlowService.Validate(f.Name, f.NodesJson, f.EdgesJson, strict: true) is { Count: > 0 } bad) return Invalid(bad);
            if (f.AccountId is null)
                return Results.Problem(title: "No account.", detail: "Choose the account this workflow trades on before switching it on.", statusCode: StatusCodes.Status409Conflict);

            var graph = FlowGraph.Parse(JsonNode.Parse(f.NodesJson) as JsonArray, JsonNode.Parse(f.EdgesJson) as JsonArray);
            string? token = null;
            if (graph.Trigger is { Type: "webhookTrigger" } && f.WebhookTokenHash is null) token = await triggers.IssueWebhookTokenAsync(f.Id, ct);
            f.IsActive = true;
            f.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            var warning = await triggers.SyncAsync(f.Id, ct);
            var view = View(f, withGraph: false, warning);
            if (token is not null) view["webhook_token"] = token;
            return Results.Ok(view);
        });

        admin.MapPost("/{id:guid}/deactivate", async (Guid id, EpTradeDbContext db, FlowTriggers triggers, CancellationToken ct) =>
        {
            var f = await db.FlowDefinitions.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Missing();
            f.IsActive = false;
            f.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await triggers.SyncAsync(f.Id, ct);
            return Results.Ok(View(f, withGraph: false));
        });

        admin.MapPost("/{id:guid}/webhook-token", async (Guid id, EpTradeDbContext db, FlowTriggers triggers, CancellationToken ct) =>
        {
            if (!await db.FlowDefinitions.AnyAsync(x => x.Id == id, ct)) return Missing();
            var token = await triggers.IssueWebhookTokenAsync(id, ct);
            return Results.Ok(new { status = "success", webhook_token = token, url = $"/api/v1/flow-webhook/{id}/{token}", message = "Keep this token: it cannot be shown again. Making a new one stops the old one working." });
        });

        admin.MapPost("/{id:guid}/run", async (Guid id, FlowService flows, CancellationToken ct) => ToHttp(await flows.RunAsync(id, "manual", null, ct)));

        admin.MapGet("/{id:guid}/runs", async (Guid id, int? limit, EpTradeDbContext db, CancellationToken ct) =>
        {
            var rows = await db.FlowRuns.AsNoTracking().Where(r => r.FlowId == id).OrderByDescending(r => r.StartedAtUtc).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
            return Results.Ok(rows.Select(r => RunView(r, withLogs: false)));
        });

        admin.MapGet("/{id:guid}/runs/{runId:guid}", async (Guid id, Guid runId, EpTradeDbContext db, CancellationToken ct) =>
            await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.FlowId == id && r.Id == runId, ct) is { } r ? Results.Ok(RunView(r, withLogs: true)) : Missing());

        // The webhook a workflow's trigger listens on. The token in the address is the credential, as TradingView and similar senders cannot set a header.
        v1.MapPost("/flow-webhook/{id:guid}/{token}", async (Guid id, string token, HttpRequest request, EpTradeDbContext db, FlowTriggers triggers, FlowService flows, CancellationToken ct) =>
        {
            var f = await db.FlowDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            // One answer for every way this can be wrong, so the address does not reveal which workflows exist.
            if (f is null || !f.IsActive || !triggers.TokenMatches(f.WebhookTokenHash, token))
                return Results.Json(new { status = "error", message = "This webhook address is not valid." }, statusCode: StatusCodes.Status404NotFound);
            var graph = FlowGraph.Parse(JsonNode.Parse(f.NodesJson) as JsonArray, JsonNode.Parse(f.EdgesJson) as JsonArray);
            if (graph.Trigger is not { Type: "webhookTrigger" })
                return Results.Json(new { status = "error", message = "This webhook address is not valid." }, statusCode: StatusCodes.Status404NotFound);

            JsonObject payload = [];
            try
            {
                if (request.ContentLength is > 0 && await JsonNode.ParseAsync(request.Body, cancellationToken: ct) is JsonObject body) payload = body;
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Json(new { status = "error", message = "The webhook body was not valid JSON." }, statusCode: StatusCodes.Status400BadRequest);
            }

            return ToHttp(await flows.RunAsync(id, "webhook", payload, ct));
        });

        return v1;
    }

    private static IResult ToHttp(FlowRunResult r)
    {
        if (r.AlreadyRunning) return Results.Json(new { status = "error", message = r.Message, already_running = true }, statusCode: StatusCodes.Status409Conflict);
        if (!r.Started) return Results.Json(new { status = "error", message = r.Message }, statusCode: StatusCodes.Status409Conflict);
        var body = new JsonObject { ["status"] = r.Succeeded ? "success" : "error", ["message"] = r.Message, ["run_id"] = r.RunId };
        if (r.Errors is { Count: > 0 }) body["errors"] = new JsonArray([.. r.Errors.Select(e => (JsonNode?)new JsonObject { ["node"] = e.Node, ["type"] = e.Type, ["message"] = e.Message })]);
        return Results.Json(body, statusCode: r.Succeeded ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult Missing() => Results.Problem(title: "Not found.", detail: "This workflow does not exist.", statusCode: StatusCodes.Status404NotFound);

    private static IResult NoAccount() => Results.Problem(title: "Not found.", detail: "This account does not exist.", statusCode: StatusCodes.Status404NotFound);

    private static IResult Invalid(List<FlowValidationError> errors) =>
        Results.Json(new { status = "error", message = errors[0].Message, errors = errors.Select(e => new { path = e.Path, code = e.Code, message = e.Message }) }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult? Parse(IScheduleSettings r, out TimeOnly? time, out TradingDays days)
    {
        time = r.ScheduleStart is { } a ? TimeOnly.ParseExact(a, "HH:mm") : null;
        days = TradingDays.Weekdays;
        if (r.ScheduleDays is null) return null;
        days = TradingDays.None;
        foreach (var raw in r.ScheduleDays)
        {
            var key = raw.Trim().ToLowerInvariant();
            var index = ScheduleFormat.DayNames.ToList().IndexOf(key.Length >= 3 ? key[..3] : key);
            if (index < 0)
                return Results.Problem(title: "Invalid days.", detail: $"'{raw}' is not a day. Use mon, tue, wed, thu, fri, sat or sun.", statusCode: StatusCodes.Status400BadRequest);
            days |= (TradingDays)(1 << index);
        }

        return null;
    }

    private static JsonObject View(FlowDefinition f, bool withGraph, string? warning = null)
    {
        var o = new JsonObject
        {
            ["id"] = f.Id, ["name"] = f.Name, ["description"] = f.Description, ["account_id"] = f.AccountId,
            ["execution_mode"] = FlowService.ModeName(f.ExecutionMode), ["exchange"] = f.Exchange, ["is_active"] = f.IsActive,
            ["is_scheduled"] = f.IsScheduled, ["schedule_time"] = f.ScheduleTimeIst?.ToString("HH:mm"),
            ["schedule_days"] = new JsonArray([.. ScheduleFormat.DayNames.Where((_, i) => ((int)f.ScheduleDays & (1 << i)) != 0).Select(d => (JsonNode?)d)]),
            ["has_webhook"] = f.WebhookTokenHash is not null, ["last_run_at_utc"] = f.LastRunAtUtc, ["last_run_status"] = f.LastRunStatus,
            ["created_at_utc"] = f.CreatedAtUtc, ["updated_at_utc"] = f.UpdatedAtUtc,
        };
        if (withGraph)
        {
            o["nodes"] = JsonNode.Parse(f.NodesJson);
            o["edges"] = JsonNode.Parse(f.EdgesJson);
        }

        if (warning is not null) o["warning"] = warning;
        return o;
    }

    private static JsonObject RunView(FlowRunRecord r, bool withLogs)
    {
        var o = new JsonObject
        {
            ["id"] = r.Id, ["flow_id"] = r.FlowId, ["status"] = r.Status, ["trigger"] = r.Trigger, ["started_at_utc"] = r.StartedAtUtc, ["finished_at_utc"] = r.FinishedAtUtc,
            ["error"] = r.Error, ["errors"] = JsonNode.Parse(r.ErrorsJson),
        };
        if (withLogs) o["logs"] = JsonNode.Parse(r.LogsJson);
        return o;
    }
}
