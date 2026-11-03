using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Validation;
using EPTradeBot.Infrastructure.OpenAlgoApi;
using EPTradeBot.Infrastructure.Security;

namespace EPTradeBot.Api.Endpoints;

/// <summary>
/// OpenAlgo's /api/v1, with the same paths, request bodies and response bodies.
/// The API key may be in the body (<c>apikey</c>) or in the <c>X-API-KEY</c> header,
/// because TradingView, Chartink and similar platforms cannot set headers. The key
/// names the account the request acts on.
/// </summary>
internal static class OpenAlgoV1Endpoints
{
    /// <summary>OpenAlgo's marshmallow schemas accept numbers sent as strings ("quantity": "10").</summary>
    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false,
    };

    public static RouteGroupBuilder MapOpenAlgoV1(this RouteGroupBuilder v1)
    {
        Post<PlaceOrderRequest>(v1, "/placeorder", (s, a, r, ct) => s.PlaceOrderAsync(a, r, ct));
        Post<PlaceSmartOrderRequest>(v1, "/placesmartorder", (s, a, r, ct) => s.PlaceSmartOrderAsync(a, r, ct));
        Post<ModifyOrderRequest>(v1, "/modifyorder", (s, a, r, ct) => s.ModifyOrderAsync(a, r, ct));
        Post<CancelOrderRequest>(v1, "/cancelorder", (s, a, r, ct) => s.CancelOrderAsync(a, r, ct));
        Post<CancelAllOrderRequest>(v1, "/cancelallorder", (s, a, r, ct) => s.CancelAllOrdersAsync(a, r, ct));
        Post<ClosePositionRequest>(v1, "/closeposition", (s, a, r, ct) => s.ClosePositionsAsync(a, r, ct));
        Post<OrderbookRequest>(v1, "/orderbook", (s, a, _, ct) => s.OrderBookAsync(a, ct));
        Post<TradebookRequest>(v1, "/tradebook", (s, a, _, ct) => s.TradeBookAsync(a, ct));
        Post<PositionbookRequest>(v1, "/positionbook", (s, a, _, ct) => s.PositionBookAsync(a, ct));
        Post<HoldingsRequest>(v1, "/holdings", (s, a, _, ct) => s.HoldingsAsync(a, ct));
        Post<FundsRequest>(v1, "/funds", (s, a, _, ct) => s.FundsAsync(a, ct));
        Post<OrderStatusRequest>(v1, "/orderstatus", (s, a, r, ct) => s.OrderStatusAsync(a, r, ct));
        Post<OpenPositionRequest>(v1, "/openposition", (s, a, r, ct) => s.OpenPositionAsync(a, r, ct));
        Post<QuotesRequest>(v1, "/quotes", (s, a, r, ct) => s.QuotesAsync(a, r, ct));
        Post<DepthRequest>(v1, "/depth", (s, a, r, ct) => s.DepthAsync(a, r, ct));
        Post<PingRequest>(v1, "/ping", (s, a, _, _) => Task.FromResult(s.Ping(a)));
        Post<AnalyzerRequest>(v1, "/analyzer", (s, a, _, _) => Task.FromResult(s.Analyzer(a)));
        Post<AnalyzerToggleRequest>(v1, "/analyzer/toggle", (s, a, _, _) => Task.FromResult(s.AnalyzerToggle(a)));
        Post<SymbolRequest>(v1, "/symbol", (s, a, r, ct) => s.SymbolAsync(a, r, ct));
        Post<SearchRequest>(v1, "/search", (s, a, r, ct) => s.SearchAsync(a, r, ct));
        Post<ExpiryRequest>(v1, "/expiry", (s, a, r, ct) => s.ExpiryAsync(a, r, ct));
        Post<OptionSymbolRequest>(v1, "/optionsymbol", (s, a, r, ct) => s.OptionSymbolAsync(a, r, ct), ValidationShape.Errors);
        Post<BasketOrderRequest>(v1, "/basketorder", (s, a, r, ct) => s.BasketOrderAsync(a, r, ct));
        Post<SplitOrderRequest>(v1, "/splitorder", (s, a, r, ct) => s.SplitOrderAsync(a, r, ct));
        Post<OptionsOrderRequest>(v1, "/optionsorder", (s, a, r, ct) => s.OptionsOrderAsync(a, r, ct), ValidationShape.Errors);
        Post<OptionsMultiOrderRequest>(v1, "/optionsmultiorder", (s, a, r, ct) => s.OptionsMultiOrderAsync(a, r, ct), ValidationShape.Errors);
        // OpenAlgo serves the instrument list on GET with the key in the query string (spreadsheets fetch it by URL).
        v1.MapGet("/instruments", (RequestDelegate)ServeInstruments).WithName("v1_instruments");
        return v1;
    }

    /// <summary>The OpenAlgo paths served so far (each also answers with a trailing slash).</summary>
    public static readonly string[] Paths =
    [
        "/placeorder", "/placesmartorder", "/modifyorder", "/cancelorder", "/cancelallorder", "/closeposition", "/orderbook", "/tradebook",
        "/positionbook", "/holdings", "/funds", "/orderstatus", "/openposition", "/quotes", "/depth", "/ping", "/analyzer", "/analyzer/toggle",
        "/symbol", "/search", "/expiry", "/optionsymbol", "/basketorder", "/splitorder", "/optionsorder", "/optionsmultiorder", "/instruments",
    ];

    /// <summary>
    /// How a request that fails validation is answered. Most endpoints put the field errors in
    /// <c>message</c>; the option endpoints answer <c>"message": "Validation error"</c> with the
    /// field errors in <c>errors</c>.
    /// </summary>
    private enum ValidationShape
    {
        Message,
        Errors,
    }

    private static void Post<T>(RouteGroupBuilder v1, string path, Func<OpenAlgoApiService, BrokerAccount, T, CancellationToken, Task<V1Result>> handler,
        ValidationShape shape = ValidationShape.Message)
        where T : ApiKeyRequest
    {
        RequestDelegate run = async context => await Handle(context, handler, shape);
        // ASP.NET Core routing ignores a trailing slash, as Flask's strict_slashes=False does.
        v1.MapPost(path, run).WithName("v1" + path.Replace('/', '_'));
    }

    private static async Task Handle<T>(HttpContext context, Func<OpenAlgoApiService, BrokerAccount, T, CancellationToken, Task<V1Result>> handler,
        ValidationShape shape, bool fromQuery = false)
        where T : ApiKeyRequest
    {
        var ct = context.RequestAborted;
        V1Result result;
        JsonObject body;
        if (fromQuery)
        {
            body = [];
            foreach (var (key, value) in context.Request.Query) body[key] = value.ToString();
            if (body["format"] is JsonValue f) body["format"] = f.GetValue<string>().ToLowerInvariant();
        }
        else
        {
            try
            {
                body = context.Request.ContentLength is 0 ? [] : await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                await Write(context, V1Result.Error(400, "The request body is not valid JSON."));
                return;
            }
        }

        if (!body.ContainsKey("apikey") && context.Request.Headers.TryGetValue("X-API-KEY", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey))
            body["apikey"] = headerKey.ToString();

        T? request;
        try
        {
            request = body.Deserialize<T>(BodyOptions);
        }
        catch (JsonException ex)
        {
            var field = ex.Path is { Length: > 2 } p ? p.TrimStart('$', '.') : "request";
            await Write(context, Invalid(shape, new JsonObject { [field] = new JsonArray("Not a valid value.") }));
            return;
        }

        if (request is null)
        {
            await Write(context, V1Result.Error(400, "The request body is empty."));
            return;
        }

        var errors = AnnotationValidator.Validate(request);
        if (errors.Count > 0)
        {
            var messages = new JsonObject();
            foreach (var (field, list) in AnnotationValidator.ToProblemErrors(errors))
                messages[field] = new JsonArray(list.Select(m => (JsonNode?)m).ToArray());
            await Write(context, Invalid(shape, messages));
            return;
        }

        var keys = context.RequestServices.GetRequiredService<ApiKeyService>();
        var account = await keys.ResolveAsync(request.ApiKey, ct);
        if (account is null)
        {
            result = V1Result.Error(403, "Invalid openalgo apikey");
        }
        else
        {
            var service = context.RequestServices.GetRequiredService<OpenAlgoApiService>();
            result = await handler(service, account, request, ct);
        }

        await Write(context, result);
    }

    private static Task ServeInstruments(HttpContext context) =>
        Handle<InstrumentsRequest>(context, static (s, a, r, ct) => s.InstrumentsAsync(a, r, ct), ValidationShape.Message, fromQuery: true);

    private static V1Result Invalid(ValidationShape shape, JsonObject fields) => shape == ValidationShape.Errors
        ? new V1Result(400, new JsonObject { ["status"] = "error", ["message"] = "Validation error", ["errors"] = fields })
        : new V1Result(400, new JsonObject { ["status"] = "error", ["message"] = fields });

    private static Task Write(HttpContext context, V1Result result)
    {
        if (result.Body["csv"] is JsonValue csv && result.Status == 200)
        {
            // /instruments?format=csv: a file download, as OpenAlgo sends it.
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/csv";
            context.Response.Headers.ContentDisposition = $"attachment; filename={result.Body["filename"]?.GetValue<string>()}";
            return context.Response.WriteAsync(csv.GetValue<string>(), context.RequestAborted);
        }

        context.Response.StatusCode = result.Status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(result.Body.ToJsonString(), context.RequestAborted);
    }
}
