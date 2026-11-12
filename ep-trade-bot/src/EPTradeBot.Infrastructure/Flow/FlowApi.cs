using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Flow;
using EPTradeBot.Infrastructure.OpenAlgoApi;

namespace EPTradeBot.Infrastructure.Flow;

/// <summary>
/// A workflow's way to the platform: the same service layer /api/v1 uses, for one account. An order goes through the guard, the kill switches and the risk limits, and
/// into the Action Center when the workflow is in semi-auto mode, exactly as one sent with an API key would. Answers are in the shape OpenAlgo's Flow client gives.
/// </summary>
public sealed class FlowApi(OpenAlgoApiService service, BrokerAccount account, string orderMode) : IFlowApi
{
    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public async Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken ct)
    {
        // The call is made as a key in the workflow's mode would be, so a semi-auto workflow's orders wait for approval.
        using var _ = ApiCallContext.Begin(new ApiCall(null, orderMode));
        V1Result result;
        try
        {
            result = await DispatchAsync(api, body, ct);
        }
        catch (JsonException ex)
        {
            return Error(400, $"The request for {api} was not understood: {ex.Message}");
        }

        return Shape(result);
    }

    private Task<V1Result> DispatchAsync(string api, JsonObject body, CancellationToken ct)
    {
        T Read<T>() => body.Deserialize<T>(Options) ?? throw new JsonException("The request is empty.");
        return api switch
        {
            "placeorder" => service.PlaceOrderAsync(account, Read<PlaceOrderRequest>(), ct),
            "placesmartorder" => service.PlaceSmartOrderAsync(account, Read<PlaceSmartOrderRequest>(), ct),
            "modifyorder" => service.ModifyOrderAsync(account, Read<ModifyOrderRequest>(), ct),
            "cancelorder" => service.CancelOrderAsync(account, Read<CancelOrderRequest>(), ct),
            "cancelallorder" => service.CancelAllOrdersAsync(account, Read<CancelAllOrderRequest>(), ct),
            "closeposition" => service.ClosePositionsAsync(account, Read<ClosePositionRequest>(), ct),
            "orderbook" => service.OrderBookAsync(account, ct),
            "tradebook" => service.TradeBookAsync(account, ct),
            "positionbook" => service.PositionBookAsync(account, ct),
            "holdings" => service.HoldingsAsync(account, ct),
            "funds" => service.FundsAsync(account, ct),
            "orderstatus" => service.OrderStatusAsync(account, Read<OrderStatusRequest>(), ct),
            "quotes" => service.QuotesAsync(account, Read<QuotesRequest>(), ct),
            "depth" => service.DepthAsync(account, Read<DepthRequest>(), ct),
            "history" => service.HistoryAsync(account, Read<HistoryRequest>(), ct),
            "openposition" => service.OpenPositionAsync(account, Read<OpenPositionRequest>(), ct),
            "multiquotes" => service.MultiQuotesAsync(account, Read<MultiQuotesRequest>(), ct),
            "symbol" => service.SymbolAsync(account, Read<SymbolRequest>(), ct),
            "search" => service.SearchAsync(account, Read<SearchRequest>(), ct),
            "expiry" => service.ExpiryAsync(account, Read<ExpiryRequest>(), ct),
            "optionsymbol" => service.OptionSymbolAsync(account, Read<OptionSymbolRequest>(), ct),
            "optionchain" => service.OptionChainAsync(account, Read<OptionChainRequest>(), ct),
            "syntheticfuture" => service.SyntheticFutureAsync(account, Read<SyntheticFutureRequest>(), ct),
            "holidays" => service.MarketHolidaysAsync(account, Read<MarketHolidaysRequest>(), ct),
            "timings" => service.MarketTimingsAsync(account, Read<MarketTimingsRequest>(), ct),
            "margin" => service.MarginAsync(account, Read<MarginCalculatorRequest>(), ct),
            "basketorder" => service.BasketOrderAsync(account, Read<BasketOrderRequest>(), ct),
            "splitorder" => service.SplitOrderAsync(account, Read<SplitOrderRequest>(), ct),
            "optionsorder" => service.OptionsOrderAsync(account, Read<OptionsOrderRequest>(), ct),
            "optionsmultiorder" => service.OptionsMultiOrderAsync(account, Read<OptionsMultiOrderRequest>(), ct),
            "telegram" => service.TelegramAlertAsync(account, body["message"]?.GetValue<string>() ?? "", ct),
            "whatsapp" => service.WhatsAppAlertAsync(account, body["message"]?.GetValue<string>() ?? "", body["to"]?.GetValue<string>(), ct),
            "analyzer" => Task.FromResult(service.Analyzer(account)),
            "intervals" => Task.FromResult(service.Intervals(account)),
            _ => Task.FromResult(V1Result.Error(404, $"Flow cannot call '{api}'.")),
        };
    }

    /// <summary>OpenAlgo's Flow client gives a success as the response with its status, and a failure as an error, its text and its code.</summary>
    private static JsonObject Shape(V1Result r)
    {
        var failed = r.Status >= 400 || r.Body["status"]?.GetValue<string>() == "error";
        if (failed) return Error(r.Status, r.Body["message"]?.GetValue<string>() ?? "Unknown error");
        var shaped = new JsonObject { ["status"] = "success" };
        foreach (var (k, v) in r.Body)
        {
            if (k != "status") shaped[k] = v?.DeepClone();
        }

        return shaped;
    }

    private static JsonObject Error(int code, string message) => new() { ["status"] = "error", ["error"] = message, ["code"] = code };
}
