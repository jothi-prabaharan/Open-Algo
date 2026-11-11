using System.Text.Json.Nodes;
using EPTradeBot.Api.Security;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Contracts.Management;
using EPTradeBot.Infrastructure.OpenAlgoApi;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Api.Endpoints;

/// <summary>
/// The Action Center: orders held for approval because their key is in semi-auto mode, and the key's mode itself. Admin-token calls; the person
/// approving is the one user of the install, recorded as <c>admin</c> in the decision log.
/// </summary>
internal static class ActionCenterEndpoints
{
    private const string Actor = "admin";

    public static RouteGroupBuilder MapActionCenter(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("").AddEndpointFilter<AdminTokenFilter>().WithDataAnnotations();

        admin.MapPut("/accounts/{accountId:guid}/apikeys/{keyId:guid}/order-mode", async (Guid accountId, Guid keyId, SetOrderModeRequest request, EpTradeDbContext db, CancellationToken ct) =>
        {
            var key = await db.AccountApiKeys.SingleOrDefaultAsync(k => k.Id == keyId && k.AccountId == accountId && k.RevokedAtUtc == null, ct);
            if (key is null) return AccountEndpoints.NotFound("No such key for this account.");
            key.OrderMode = request.Mode;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "success", mode = key.OrderMode });
        });

        admin.MapGet("/action-center/orders", async (string? status, Guid? account_id, ActionCenterStore store, CancellationToken ct) =>
        {
            var wanted = string.IsNullOrEmpty(status) || status == "all" ? null : status;
            var rows = await store.ListAsync(account_id, wanted, ct);
            return Results.Ok(new { status = "success", data = rows.Select(Row).ToList() });
        });

        admin.MapGet("/action-center/orders/{id:int}", async (int id, ActionCenterStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } row ? Results.Ok(new { status = "success", data = Row(row) }) : AccountEndpoints.NotFound("No such order in the Action Center."));

        admin.MapPost("/action-center/orders/{id:int}/approve", async (int id, OpenAlgoApiService service, CancellationToken ct) =>
        {
            var d = await service.ApprovePendingAsync(id, Actor, bulk: false, ct);
            return Results.Json(d.Body, statusCode: d.Status);
        });

        admin.MapPost("/action-center/orders/{id:int}/reject", async (int id, RejectOrderRequest request, OpenAlgoApiService service, CancellationToken ct) =>
        {
            var d = await service.RejectPendingAsync(id, Actor, request.Reason, bulk: false, ct);
            return Results.Json(d.Body, statusCode: d.Status);
        });

        admin.MapPost("/action-center/approve-all", async (ApproveAllRequest request, ActionCenterStore store, OpenAlgoApiService service, CancellationToken ct) =>
        {
            var results = new JsonArray();
            // One at a time and in the order they were queued, so each one meets the limits the one before it left.
            foreach (var row in (await store.ListAsync(request.AccountId, ActionCenterStore.Pending, ct)).OrderBy(r => r.Id))
            {
                var d = await service.ApprovePendingAsync(row.Id, Actor, bulk: true, ct);
                results.Add(new JsonObject { ["pending_order_id"] = row.Id, ["http_status"] = d.Status, ["result"] = d.Body.DeepClone() });
            }

            return Results.Ok(new { status = "success", count = results.Count, results });
        });

        admin.MapPost("/action-center/reject-all", async (RejectOrderRequest request, ActionCenterStore store, OpenAlgoApiService service, CancellationToken ct) =>
        {
            var results = new JsonArray();
            foreach (var row in (await store.ListAsync(request.AccountId, ActionCenterStore.Pending, ct)).OrderBy(r => r.Id))
            {
                var d = await service.RejectPendingAsync(row.Id, Actor, request.Reason, bulk: true, ct);
                results.Add(new JsonObject { ["pending_order_id"] = row.Id, ["http_status"] = d.Status });
            }

            return Results.Ok(new { status = "success", count = results.Count, results });
        });

        return v1;
    }

    private static object Row(EPTradeBot.Core.Entities.Legacy.Audit.ActionCenterPendingOrder o) => new
    {
        id = o.Id, account_id = o.UserId, api_type = o.ApiType, status = o.Status, order_data = JsonNode.Parse(o.OrderData),
        created_at_ist = o.CreatedAtIst, approved_at_ist = o.ApprovedAtIst, approved_by = o.ApprovedBy, rejected_at_ist = o.RejectedAtIst,
        rejected_by = o.RejectedBy, reason = o.RejectedReason, broker_order_id = o.BrokerOrderId, broker_status = o.BrokerStatus,
    };
}
