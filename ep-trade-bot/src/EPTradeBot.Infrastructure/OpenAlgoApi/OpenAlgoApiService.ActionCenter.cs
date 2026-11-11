using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Audit;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// The Action Center (OpenAlgo's order_router_service.py and pending_order_execution_service.py). A key in <c>semi_auto</c> mode does not send its
/// orders: they are held until somebody approves them. Approval replays the stored request through the same service as if it had just arrived, so
/// the environment guard, the kill switch and the account's risk limits are decided again then, not only when the order was queued. In semi-auto
/// the calls that would change or undo something already live (modify, cancel, close) are refused outright, as OpenAlgo refuses them.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private static readonly JsonSerializerOptions StoredRequest = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    /// <summary>Holds the request for approval when the key is in semi-auto mode. Null when the call should go on and be sent.</summary>
    private async Task<V1Result?> QueueIfSemiAutoAsync<T>(BrokerAccount account, string apiType, T request, CancellationToken ct) where T : ApiKeyRequest
    {
        if (!ApiCallContext.Get().IsSemiAuto) return null;
        // An order the environment would refuse anyway is not worth a person's time.
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        var body = JsonSerializer.SerializeToNode(request, StoredRequest)!.AsObject();
        body.Remove("apikey");
        var id = await actionCenter.QueueAsync(account.Id, apiType, body.ToJsonString(), ct);
        return new V1Result(200, new JsonObject
        {
            ["status"] = "success", ["message"] = "Order queued for approval in Action Center", ["mode"] = "semi_auto", ["pending_order_id"] = id,
        });
    }

    private static V1Result? SemiAutoBlocked(string operation, string verb) =>
        ApiCallContext.Get().IsSemiAuto ? V1Result.Error(403, $"{operation} operation is not allowed in Semi-Auto mode. Please switch to Auto mode to {verb}.") : null;

    // ---- decisions -------------------------------------------------------------------------------------------

    public sealed record Decided(int Id, int Status, JsonObject Body);

    private async Task<BrokerAccount?> AccountOfAsync(ActionCenterPendingOrder order, CancellationToken ct)
    {
        if (!Guid.TryParseExact(order.UserId, "N", out var id)) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
    }

    /// <summary>Approves one order: claims it, replays it through the guard and the risk check as of now, and records what came of it.</summary>
    public async Task<Decided> ApprovePendingAsync(int id, string actor, bool bulk, CancellationToken ct)
    {
        await actionCenter.ExpireStaleAsync(ct);
        var order = await actionCenter.GetAsync(id, ct);
        if (order is null) return new Decided(id, 404, V1Result.Error(404, "No such order in the Action Center.").Body);
        if (!await actionCenter.ClaimAsync(id, ct))
            return new Decided(id, 409, V1Result.Error(409, $"This order is already {order.Status}, so it cannot be approved now.").Body);

        var account = await AccountOfAsync(order, ct);
        if (account is null)
        {
            await actionCenter.FinishApprovalAsync(id, null, actor, false, null, "The account this order was for no longer exists.", bulk, ct);
            return new Decided(id, 404, V1Result.Error(404, "The account this order was for no longer exists.").Body);
        }

        V1Result result;
        try
        {
            result = await ReplayAsync(account, order.ApiType, order.OrderData, actor, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            logger.LogError(ex, "Queued order {Id} could not be read back.", id);
            result = V1Result.Error(500, "The queued order could not be read back, so it was not sent.");
        }

        var sent = result.Status == 200;
        var message = result.Body["message"]?.GetValue<string>() ?? "The order was not sent.";
        var brokerOrderId = result.Body["orderid"]?.ToString() ?? result.Body["trigger_id"]?.ToString();
        await actionCenter.FinishApprovalAsync(id, account.Id, actor, sent, brokerOrderId, message, bulk, ct);
        var body = (JsonObject)result.Body.DeepClone();
        body["pending_order_id"] = id;
        return new Decided(id, result.Status, body);
    }

    public async Task<Decided> RejectPendingAsync(int id, string actor, string? reason, bool bulk, CancellationToken ct)
    {
        await actionCenter.ExpireStaleAsync(ct);
        var order = await actionCenter.GetAsync(id, ct);
        if (order is null) return new Decided(id, 404, V1Result.Error(404, "No such order in the Action Center.").Body);
        Guid? account = Guid.TryParseExact(order.UserId, "N", out var parsed) ? parsed : null;
        return await actionCenter.RejectAsync(id, account, actor, reason, bulk, ct)
            ? new Decided(id, 200, new JsonObject { ["status"] = "success", ["message"] = "Order rejected", ["pending_order_id"] = id })
            : new Decided(id, 409, V1Result.Error(409, $"This order is already {order.Status}, so it cannot be rejected now.").Body);
    }

    /// <summary>Runs a queued request as if it had just arrived in auto mode, from the Action Center.</summary>
    private async Task<V1Result> ReplayAsync(BrokerAccount account, string apiType, string json, string actor, CancellationToken ct)
    {
        T Read<T>() => JsonSerializer.Deserialize<T>(json, StoredRequest) ?? throw new JsonException("The stored request is empty.");
        using var _ = ApiCallContext.Begin(new ApiCall(null, "auto", actor));
        return apiType switch
        {
            "placeorder" => await PlaceOrderAsync(account, Read<PlaceOrderRequest>(), ct),
            "placesmartorder" => await PlaceSmartOrderAsync(account, Read<PlaceSmartOrderRequest>(), ct),
            "basketorder" => await BasketOrderAsync(account, Read<BasketOrderRequest>(), ct),
            "splitorder" => await SplitOrderAsync(account, Read<SplitOrderRequest>(), ct),
            "optionsorder" => await OptionsOrderAsync(account, Read<OptionsOrderRequest>(), ct),
            "optionsmultiorder" => await OptionsMultiOrderAsync(account, Read<OptionsMultiOrderRequest>(), ct),
            "placegttorder" => await PlaceGttOrderAsync(account, Read<PlaceGttOrderRequest>(), ct),
            _ => V1Result.Error(400, $"Orders of type {apiType} cannot be replayed from the Action Center."),
        };
    }
}
