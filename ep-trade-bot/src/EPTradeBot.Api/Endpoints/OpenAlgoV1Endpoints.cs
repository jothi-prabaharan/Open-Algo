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
        Post<MultiQuotesRequest>(v1, "/multiquotes", (s, a, r, ct) => s.MultiQuotesAsync(a, r, ct));
        Post<SyntheticFutureRequest>(v1, "/syntheticfuture", (s, a, r, ct) => s.SyntheticFutureAsync(a, r, ct), ValidationShape.Errors);
        Post<OptionChainRequest>(v1, "/optionchain", (s, a, r, ct) => s.OptionChainAsync(a, r, ct), ValidationShape.Errors);
        Post<OptionGreeksRequest>(v1, "/optiongreeks", (s, a, r, ct) => s.OptionGreeksAsync(a, r, ct), ValidationShape.Errors);
        Post<MultiOptionGreeksRequest>(v1, "/multioptiongreeks", (s, a, r, ct) => s.MultiOptionGreeksAsync(a, r, ct), ValidationShape.Errors);
        Post<BasketOrderRequest>(v1, "/basketorder", (s, a, r, ct) => s.BasketOrderAsync(a, r, ct));
        Post<SplitOrderRequest>(v1, "/splitorder", (s, a, r, ct) => s.SplitOrderAsync(a, r, ct));
        Post<OptionsOrderRequest>(v1, "/optionsorder", (s, a, r, ct) => s.OptionsOrderAsync(a, r, ct), ValidationShape.Errors);
        Post<OptionsMultiOrderRequest>(v1, "/optionsmultiorder", (s, a, r, ct) => s.OptionsMultiOrderAsync(a, r, ct), ValidationShape.Errors);
        Post<PlaceGttOrderRequest>(v1, "/placegttorder", (s, a, r, ct) => s.PlaceGttOrderAsync(a, r, ct));
        Post<ModifyGttOrderRequest>(v1, "/modifygttorder", (s, a, r, ct) => s.ModifyGttOrderAsync(a, r, ct));
        Post<CancelGttOrderRequest>(v1, "/cancelgttorder", (s, a, r, ct) => s.CancelGttOrderAsync(a, r, ct));
        Post<GttOrderBookRequest>(v1, "/gttorderbook", (s, a, r, ct) => s.GttOrderBookAsync(a, r, ct));
        Post<MarginCalculatorRequest>(v1, "/margin", (s, a, r, ct) => s.MarginAsync(a, r, ct), beforeKey: OpenAlgoApiService.ValidateMargin);
        Post<HistoryRequest>(v1, "/history", (s, a, r, ct) => s.HistoryAsync(a, r, ct));
        Post<IntervalsRequest>(v1, "/intervals", (s, a, _, _) => Task.FromResult(s.Intervals(a)));
        Post<MarketHolidaysRequest>(v1, "/market/holidays", (s, a, r, ct) => s.MarketHolidaysAsync(a, r, ct));
        Post<MarketTimingsRequest>(v1, "/market/timings", (s, a, r, ct) => s.MarketTimingsAsync(a, r, ct));
        Post<PnlSymbolsRequest>(v1, "/pnl/symbols", (s, a, _, ct) => s.PnlSymbolsAsync(a, ct));
        v1.MapGet("/ticker/{symbol}", (RequestDelegate)ServeTicker).WithName("v1_ticker");
        v1.MapPost("/chart", (RequestDelegate)SaveChart).WithName("v1_chart");
        v1.MapGet("/chart", (RequestDelegate)ServeChart).WithName("v1_chart_get");
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
        "/multiquotes", "/syntheticfuture", "/optionchain", "/optiongreeks", "/multioptiongreeks", "/market/holidays", "/market/timings", "/pnl/symbols", "/chart", "/history", "/intervals", "/ticker/{symbol}", "/margin",
        "/placegttorder", "/modifygttorder", "/cancelgttorder", "/gttorderbook",
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
        ValidationShape shape = ValidationShape.Message, Func<T, V1Result?>? beforeKey = null)
        where T : ApiKeyRequest
    {
        RequestDelegate run = async context => await Handle(context, handler, shape, beforeKey: beforeKey);
        // ASP.NET Core routing ignores a trailing slash, as Flask's strict_slashes=False does.
        v1.MapPost(path, run).WithName("v1" + path.Replace('/', '_'));
    }

    /// <param name="beforeValidation">An answer OpenAlgo gives before it validates the body (an empty body, a missing key).</param>
    /// <param name="beforeKey">An answer OpenAlgo gives after validation and before it checks the API key.</param>
    private static async Task Handle<T>(HttpContext context, Func<OpenAlgoApiService, BrokerAccount, T, CancellationToken, Task<V1Result>> handler,
        ValidationShape shape, bool fromQuery = false, Func<HttpContext, JsonObject, V1Result?>? beforeValidation = null, Func<T, V1Result?>? beforeKey = null,
        Func<HttpContext, JsonObject>? bodyFactory = null)
        where T : ApiKeyRequest
    {
        var ct = context.RequestAborted;
        V1Result result;
        JsonObject body;
        if (bodyFactory is not null)
        {
            body = bodyFactory(context);
        }
        else if (fromQuery)
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

        // Before the header key is added: OpenAlgo looks at the body it was sent.
        if (beforeValidation?.Invoke(context, body) is { } early)
        {
            await Write(context, early);
            return;
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
            {
                var array = new JsonArray(list.Select(m => (JsonNode?)m).ToArray());
                // An error inside a list is nested as the schema library does: symbols[0].exchange is {"symbols": {"0": {"exchange": [...]}}}.
                if (System.Text.RegularExpressions.Regex.Match(field, @"^(\w+)\[(\d+)\]\.(\w+)$") is { Success: true } nested)
                {
                    var outer = messages[nested.Groups[1].Value] as JsonObject ?? new JsonObject();
                    messages[nested.Groups[1].Value] = outer;
                    var item = outer[nested.Groups[2].Value] as JsonObject ?? new JsonObject();
                    outer[nested.Groups[2].Value] = item;
                    item[nested.Groups[3].Value] = array;
                }
                else
                {
                    messages[field] = array;
                }
            }

            await Write(context, Invalid(shape, messages));
            return;
        }

        if (beforeKey?.Invoke(request) is { } refused)
        {
            await Write(context, refused);
            return;
        }

        var keys = context.RequestServices.GetRequiredService<ApiKeyService>();
        var call = await keys.ResolveCallAsync(request.ApiKey, ct);
        if (call is null)
        {
            result = V1Result.Error(403, "Invalid openalgo apikey");
        }
        else
        {
            var service = context.RequestServices.GetRequiredService<OpenAlgoApiService>();
            using var _ = ApiCallContext.Begin(new ApiCall(call.Value.KeyId, call.Value.OrderMode));
            result = await handler(service, call.Value.Account, request, ct);
        }

        await Write(context, result);
    }

    private static Task ServeInstruments(HttpContext context) =>
        Handle<InstrumentsRequest>(context, static (s, a, r, ct) => s.InstrumentsAsync(a, r, ct), ValidationShape.Message, fromQuery: true);

    /// <summary>
    /// GET /api/v1/ticker/EXCHANGE:SYMBOL?interval=&amp;from=&amp;to=&amp;apikey=&amp;format=json|txt. The query names are OpenAlgo's;
    /// the request is checked as a history request, as OpenAlgo checks it. OpenAlgo reads a symbol written without an exchange as
    /// NSE:RELIANCE whatever it was; EP Trade bot asks for the exchange instead of answering for another instrument.
    /// </summary>
    private static Task ServeTicker(HttpContext context)
    {
        var text = string.Equals(context.Request.Query["format"], "txt", StringComparison.OrdinalIgnoreCase);
        // OpenAlgo answers a txt request's errors as plain text; the flag lets Write do the same.
        if (text) context.Items["text-errors"] = true;
        return Handle<HistoryRequest>(context, async (s, a, r, ct) => await s.TickerAsync(a, r, text, ct), ValidationShape.Message,
            beforeValidation: static (ctx, _) => ((string?)ctx.Request.RouteValues["symbol"] ?? "").Split(':').Length == 2
                ? null
                : V1Result.Error(400, "Write the symbol as EXCHANGE:SYMBOL, for example NSE:RELIANCE."),
            bodyFactory: static ctx =>
            {
                var parts = ((string?)ctx.Request.RouteValues["symbol"] ?? "").Split(':');
                var query = ctx.Request.Query;
                var body = new JsonObject { ["symbol"] = parts.Length == 2 ? parts[1] : "", ["exchange"] = parts.Length == 2 ? parts[0] : "", ["interval"] = query["interval"].FirstOrDefault() ?? "D" };
                if (query["apikey"].FirstOrDefault() is { } key) body["apikey"] = key;
                if (query["from"].FirstOrDefault() is { } from) body["start_date"] = from;
                if (query["to"].FirstOrDefault() is { } to) body["end_date"] = to;
                return body;
            });
    }

    /// <summary>
    /// GET /api/v1/chart?apikey=...: the saved chart preferences. OpenAlgo reads the key from the
    /// query string only; EP Trade bot also takes the X-API-KEY header, which keeps the key out of
    /// URLs and their logs.
    /// </summary>
    private static Task ServeChart(HttpContext context) =>
        Handle<ChartGetRequest>(context, static (s, a, _, ct) => s.ChartPreferencesAsync(a, ct), ValidationShape.Message, fromQuery: true,
            beforeValidation: static (context, body) =>
                (body["apikey"] is JsonValue k && k.GetValue<string>().Length > 0) || !string.IsNullOrWhiteSpace(context.Request.Headers["X-API-KEY"])
                    ? null
                    : V1Result.Error(400, "Missing apikey parameter"));

    /// <summary>POST /api/v1/chart: saves the preferences sent beside the key (any keys, within OpenAlgo's limits).</summary>
    private static Task SaveChart(HttpContext context) =>
        Handle<ChartRequest>(context, static (s, a, r, ct) => s.SaveChartPreferencesAsync(a, r, ct), ValidationShape.Message,
            beforeValidation: static (_, body) => body.Count == 0 ? V1Result.Error(400, "No data provided") : null,
            beforeKey: static r => OpenAlgoApiService.ChartLimits(r.Preferences));

    private static V1Result Invalid(ValidationShape shape, JsonObject fields) => shape == ValidationShape.Errors
        ? new V1Result(400, new JsonObject { ["status"] = "error", ["message"] = "Validation error", ["errors"] = fields })
        : new V1Result(400, new JsonObject { ["status"] = "error", ["message"] = fields });

    private static Task Write(HttpContext context, V1Result result)
    {
        if (context.Items.ContainsKey("text-errors") && result.Status >= 400 && result.Body["message"] is JsonValue reason && reason.TryGetValue<string>(out var sentence))
        {
            // The ticker's txt format answers its errors as plain text, a line for the key and the module, bare for the rest.
            context.Response.StatusCode = result.Status;
            context.Response.ContentType = "text/plain";
            return context.Response.WriteAsync(result.Status is 403 or 404 ? sentence + "\n" : sentence, context.RequestAborted);
        }

        if (result.Body["text"] is JsonValue plain && result.Status == 200)
        {
            // /ticker?format=txt: plain lines, no JSON.
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
            return context.Response.WriteAsync(plain.GetValue<string>(), context.RequestAborted);
        }

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
