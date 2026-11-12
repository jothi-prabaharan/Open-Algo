using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Flow;
using EPTradeBot.Core.Indicators;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Agent;

/// <summary>One call the assistant made, kept so the trader can see what it did.</summary>
public sealed record AgentToolCall(string Name, string Arguments, bool Ok, string Summary);

/// <summary>Whether an order was put in the Action Center, and under what id.</summary>
public sealed record ProposalResult(bool Ok, int? Id, string Message);

/// <summary>
/// What the assistant can do, for one account. Everything it reads goes through the platform's own API; nothing it can call sends an order. The order tools
/// only <b>propose</b>: they put the order in the Action Center, where a person approves it and the guard, the kill switches and the risk limits are decided
/// again. There is no tool that places, modifies or cancels anything, so a model that is mistaken, or has been told what to do by text in a quote or a symbol
/// name, can at worst fill the approval queue (and the queue is capped).
/// </summary>
public sealed class AgentToolbox(IFlowApi api, Func<PlaceOrderRequest, CancellationToken, Task<ProposalResult>> propose, int maxProposals, TimeProvider clock, ILogger logger)
{
    private const int MaxReplyChars = 6000;
    private const int MaxHistoryRows = 100;

    private readonly List<AgentToolCall> calls = [];
    private readonly List<int> proposals = [];
    private int proposed;

    public IReadOnlyList<AgentToolCall> Calls => calls;

    public IReadOnlyList<int> Proposals => proposals;

    public IList<AITool> Tools() =>
    [
        AIFunctionFactory.Create(GetQuote, "get_quote", "The latest price and day range of one instrument."),
        AIFunctionFactory.Create(GetDepth, "get_depth", "The best bids and offers of one instrument."),
        AIFunctionFactory.Create(GetHistory, "get_history", "Recent candles of one instrument (open, high, low, close, volume)."),
        AIFunctionFactory.Create(ComputeIndicator, "compute_indicator", "One technical indicator over an instrument's recent candles. Use list_indicators for the names."),
        AIFunctionFactory.Create(ListIndicators, "list_indicators", "The names of the indicators compute_indicator knows."),
        AIFunctionFactory.Create(GetFunds, "get_funds", "The account's cash and margin."),
        AIFunctionFactory.Create(GetPositions, "get_positions", "The account's open positions."),
        AIFunctionFactory.Create(GetHoldings, "get_holdings", "The account's long-term holdings."),
        AIFunctionFactory.Create(GetOrders, "get_orders", "Today's orders and their status."),
        AIFunctionFactory.Create(GetTrades, "get_trades", "Today's executed trades."),
        AIFunctionFactory.Create(ProposeOrder, "propose_order",
            "Suggest an order. It is NOT sent: it waits in the Action Center until the trader approves it, and the risk checks run again then. Give the reason."),
        AIFunctionFactory.Create(ProposeClose, "propose_close", "Suggest squaring off one open position. Not sent: it waits for the trader's approval."),
    ];

    // ---- reading -----------------------------------------------------------------------------------------

    private Task<string> GetQuote([Description("The symbol, e.g. SBIN")] string symbol, [Description("NSE, NFO, BSE, MCX, ...")] string exchange = "NSE") =>
        Read("get_quote", new { symbol, exchange }, () => api.CallAsync("quotes", new JsonObject { ["symbol"] = symbol.Trim().ToUpperInvariant(), ["exchange"] = exchange.Trim().ToUpperInvariant() }, default));

    private Task<string> GetDepth(string symbol, string exchange = "NSE") =>
        Read("get_depth", new { symbol, exchange }, () => api.CallAsync("depth", new JsonObject { ["symbol"] = symbol.Trim().ToUpperInvariant(), ["exchange"] = exchange.Trim().ToUpperInvariant() }, default));

    private Task<string> GetFunds() => Read("get_funds", new { }, () => api.CallAsync("funds", [], default));

    private Task<string> GetPositions() => Read("get_positions", new { }, () => api.CallAsync("positionbook", [], default));

    private Task<string> GetHoldings() => Read("get_holdings", new { }, () => api.CallAsync("holdings", [], default));

    private Task<string> GetOrders() => Read("get_orders", new { }, () => api.CallAsync("orderbook", [], default));

    private Task<string> GetTrades() => Read("get_trades", new { }, () => api.CallAsync("tradebook", [], default));

    private Task<string> GetHistory(
        string symbol, string exchange = "NSE", [Description("1m, 5m, 15m, 1h or D")] string interval = "D", [Description("How many candles, up to 100")] int bars = 50) =>
        Read("get_history", new { symbol, exchange, interval, bars }, async () =>
        {
            var result = await FetchHistoryAsync(symbol, exchange, interval, Math.Clamp(bars, 1, MaxHistoryRows));
            if (result["data"] is JsonArray rows && rows.Count > MaxHistoryRows) result["data"] = new JsonArray([.. rows.Skip(rows.Count - MaxHistoryRows).Select(r => r?.DeepClone())]);
            return result;
        });

    private Task<string> ComputeIndicator(
        [Description("An indicator name from list_indicators, e.g. rsi, sma, macd")] string indicator,
        string symbol, string exchange = "NSE", string interval = "D",
        [Description("A JSON object of the indicator's settings, e.g. {\"period\": 14}")] string settings = "{}",
        [Description("How many candles to look back, up to 200")] int lookbackBars = 100) =>
        Read("compute_indicator", new { indicator, symbol, exchange, interval, settings, lookbackBars }, async () =>
        {
            JsonObject parameters;
            try
            {
                parameters = (string.IsNullOrWhiteSpace(settings) ? [] : JsonNode.Parse(settings) as JsonObject) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return Failure("The indicator settings must be a JSON object such as {\"period\": 14}.");
            }

            var bars = IndicatorNode.ClampBars(lookbackBars);
            var history = await FetchHistoryAsync(symbol, exchange, interval, bars);
            if (!FlowApiResults.IsSuccess(history)) return history;
            var records = (history["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            if (records.Count > IndicatorNode.MaxBars) records = records[^IndicatorNode.MaxBars..];
            try
            {
                return IndicatorNode.Compute(records, indicator, parameters, bars, 5, null, 0);
            }
            catch (ArgumentException ex)
            {
                return Failure(ex.Message);
            }
        });

    private Task<string> ListIndicators() => Read("list_indicators", new { }, () => Task.FromResult(new JsonObject
    {
        ["status"] = "success",
        ["indicators"] = new JsonArray([.. IndicatorRegistry.Names.Where(n => IndicatorInputs.All.ContainsKey(n)).Select(n => (JsonNode?)n)]),
    }));

    private async Task<JsonObject> FetchHistoryAsync(string symbol, string exchange, string interval, int bars)
    {
        var end = DateOnly.FromDateTime(EPTradeBot.Core.Time.Ist.FromUtc(clock.GetUtcNow().UtcDateTime));
        var start = end.AddDays(-IndicatorNode.CalendarDays(bars, interval));
        return await api.CallAsync("history", new JsonObject
        {
            ["symbol"] = symbol.Trim().ToUpperInvariant(), ["exchange"] = exchange.Trim().ToUpperInvariant(), ["interval"] = interval, ["source"] = "api",
            ["start_date"] = start.ToString("yyyy-MM-dd"), ["end_date"] = end.ToString("yyyy-MM-dd"),
        }, default);
    }

    // ---- proposing ---------------------------------------------------------------------------------------

    private Task<string> ProposeOrder(
        string symbol, string exchange, [Description("BUY or SELL")] string action, [Description("Whole units, at least 1")] int quantity,
        [Description("MARKET, LIMIT, SL or SL-M")] string pricetype = "MARKET", [Description("MIS, NRML or CNC; leave out for the usual one")] string? product = null,
        double price = 0, double triggerPrice = 0, [Description("Why: one or two sentences the trader will read before approving")] string rationale = "") =>
        Write("propose_order", new { symbol, exchange, action, quantity, pricetype, product, price, triggerPrice, rationale }, async () =>
        {
            var order = Validate(symbol, exchange, action, quantity, pricetype, product, price, triggerPrice);
            if (order is string problem) return Failure(problem);
            return await SubmitAsync((PlaceOrderRequest)order, rationale);
        });

    private Task<string> ProposeClose(string symbol, string exchange = "NSE", string? product = null, [Description("Why")] string rationale = "") =>
        Write("propose_close", new { symbol, exchange, product, rationale }, async () =>
        {
            var sym = symbol.Trim().ToUpperInvariant();
            var exch = exchange.Trim().ToUpperInvariant();
            var book = await api.CallAsync("positionbook", [], default);
            if (!FlowApiResults.IsSuccess(book)) return book;
            var held = (book["data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p =>
                Text(p, "symbol") == sym && Text(p, "exchange") == exch && (product is null || Text(p, "product") == product.ToUpperInvariant())
                && p["quantity"] is JsonValue q && FlowData.TryParseOrNumber(q, out var n) && n != 0);
            if (held is null) return Failure($"There is no open position in {sym} to close.");
            FlowData.TryParseOrNumber((JsonValue)held["quantity"]!, out var quantity);
            var order = Validate(sym, exch, quantity > 0 ? "SELL" : "BUY", (int)Math.Abs(quantity), "MARKET", Text(held, "product"), 0, 0);
            if (order is string problem) return Failure(problem);
            return await SubmitAsync((PlaceOrderRequest)order, rationale);
        });

    /// <summary>The order as a request, or the reason it cannot be proposed. The same words and rules a workflow's order node uses.</summary>
    private static object Validate(string symbol, string exchange, string action, int quantity, string pricetype, string? product, double price, double triggerPrice)
    {
        var sym = (symbol ?? "").Trim().ToUpperInvariant();
        var exch = (exchange ?? "").Trim().ToUpperInvariant();
        var act = (action ?? "").Trim().ToUpperInvariant();
        var type = string.IsNullOrWhiteSpace(pricetype) ? "MARKET" : pricetype.Trim().ToUpperInvariant();
        var prod = string.IsNullOrWhiteSpace(product) ? OrderVocabulary.DefaultProduct(exch) : product.Trim().ToUpperInvariant();
        if (sym.Length == 0 || sym.Length > 64) return "The symbol is missing or too long.";
        if (!OrderVocabulary.Exchanges.Contains(exch)) return $"The exchange must be one of {string.Join(", ", OrderVocabulary.Exchanges)}.";
        if (!OrderVocabulary.Actions.Contains(act)) return "The action must be BUY or SELL.";
        if (!OrderVocabulary.PriceTypes.Contains(type)) return $"The price type must be one of {string.Join(", ", OrderVocabulary.PriceTypes)}.";
        if (!OrderVocabulary.Products.Contains(prod)) return $"The product must be one of {string.Join(", ", OrderVocabulary.Products)}.";
        if (quantity < 1) return "The quantity must be at least 1.";
        if (!double.IsFinite(price) || !double.IsFinite(triggerPrice) || price < 0 || triggerPrice < 0) return "Prices cannot be negative.";
        if (OrderVocabulary.InvalidPriceReason(type, price, triggerPrice) is { } invalid) return invalid;
        return new PlaceOrderRequest { Symbol = sym, Exchange = exch, Action = act, Quantity = quantity, PriceType = type, Product = prod, Price = (decimal)price, TriggerPrice = (decimal)triggerPrice, Strategy = "agent" };
    }

    private async Task<JsonObject> SubmitAsync(PlaceOrderRequest request, string rationale)
    {
        if (proposed >= maxProposals)
            return Failure($"Only {maxProposals} orders can be proposed in one answer. Tell the trader what else you would suggest, and let them decide.");
        var result = await propose(request, default);
        if (!result.Ok) return Failure(result.Message);
        proposed++;
        if (result.Id is { } id) proposals.Add(id);
        return new JsonObject
        {
            ["status"] = "success", ["proposal_id"] = result.Id, ["state"] = "waiting for the trader's approval",
            ["message"] = "The order was NOT sent. It is in the Action Center, where the trader approves or rejects it.", ["rationale"] = rationale,
        };
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private Task<string> Read(string name, object arguments, Func<Task<JsonObject>> call) => Run(name, arguments, call);

    private Task<string> Write(string name, object arguments, Func<Task<JsonObject>> call) => Run(name, arguments, call);

    private async Task<string> Run(string name, object arguments, Func<Task<JsonObject>> call)
    {
        var args = JsonSerializer.Serialize(arguments);
        JsonObject result;
        try
        {
            result = await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The assistant's tool {Tool} failed.", name);
            result = Failure("That could not be done right now.");
        }

        var ok = FlowApiResults.IsSuccess(result);
        var text = result.ToJsonString();
        if (text.Length > MaxReplyChars) text = text[..MaxReplyChars] + "... (cut short)";
        calls.Add(new AgentToolCall(name, args, ok, ok ? "ok" : FlowApiResults.Reason(result, "failed")));
        return text;
    }

    private static JsonObject Failure(string message) => new() { ["status"] = "error", ["message"] = message };

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v ? FlowContext.PyStr(v) : "";
}
