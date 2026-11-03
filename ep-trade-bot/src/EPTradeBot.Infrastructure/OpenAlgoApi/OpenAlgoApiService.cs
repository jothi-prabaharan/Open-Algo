using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Brokers;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>An /api/v1 answer: the HTTP status and OpenAlgo's JSON body.</summary>
public sealed record V1Result(int Status, JsonObject Body)
{
    public static V1Result Error(int status, string message) => new(status, new JsonObject { ["status"] = "error", ["message"] = message });
}

/// <summary>
/// The service layer behind /api/v1, ported from OpenAlgo's services/*_service.py.
/// The API key names the account. A PAPER account answers from the sandbox (OpenAlgo's
/// analyze mode, marked <c>"mode": "analyze"</c>); a LIVE account answers from its
/// broker plugin. Every order goes through the order dispatcher, so the environment
/// guard and the account's risk limits apply to API orders exactly as to any other.
/// Every other write to a broker (modify, cancel) is checked by the guard first.
/// </summary>
public sealed class OpenAlgoApiService(
    IOrderDispatcher dispatcher,
    ITradingGuardService guard,
    IBrokerRegistry registry,
    BrokerSessionFactory sessions,
    SandboxEngine sandbox,
    KeyedAsyncLock<string> instrumentLocks,
    ISymbolMaster symbols,
    ILogger<OpenAlgoApiService> logger)
{
    // ---- orders ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Orders carry whole quantities in this build. A fractional crypto quantity is refused
    /// plainly rather than truncated, which would send a different order from the one asked for.
    /// </summary>
    private static V1Result? Fractional(BrokerAccount account, decimal quantity) =>
        quantity == decimal.Truncate(quantity) ? null
        : Mark(account, V1Result.Error(400, $"Fractional quantities ({quantity}) are not supported in EP Trade bot yet. Use a whole quantity, or place this order on {account.Broker} directly."));

    /// <summary>
    /// For a LIVE account, a symbol the broker's master contract does not have is refused by
    /// name, before the risk check (which would otherwise report a missing price).
    /// </summary>
    private async Task<V1Result?> UnknownSymbolAsync(BrokerAccount account, string symbol, string exchange, CancellationToken ct)
    {
        if (account.AccountType != AccountType.LIVE || registry.Find(account.Broker) is not { } plugin) return null;
        if (await symbols.FindAsync(plugin.Name, symbol, exchange, ct) is not null) return null;
        return Mark(account, V1Result.Error(400, $"{symbol} on {exchange} is not in the {plugin.DisplayName} master contract. Check the symbol, or download the master contract again."));
    }

    public async Task<V1Result> PlaceOrderAsync(BrokerAccount account, PlaceOrderRequest r, CancellationToken ct)
    {
        if (Fractional(account, r.Quantity) is { } fractional) return fractional;
        if (await UnknownSymbolAsync(account, r.Symbol, r.Exchange, ct) is { } unknown) return unknown;
        var result = await dispatcher.DispatchAsync(Intent(account, r.Strategy, r.Symbol, r.Exchange, r.Action, r.Quantity, r.PriceType, r.Product, r.Price, r.TriggerPrice, r.DisclosedQuantity), ct);
        return OrderAnswer(account, result);
    }

    public async Task<V1Result> PlaceSmartOrderAsync(BrokerAccount account, PlaceSmartOrderRequest r, CancellationToken ct)
    {
        if ((Fractional(account, r.Quantity) ?? Fractional(account, r.PositionSize ?? 0)) is { } fractional) return fractional;
        if (await UnknownSymbolAsync(account, r.Symbol, r.Exchange, ct) is { } unknown) return unknown;
        // One smart order per instrument at a time, so each one reads the position the previous one left.
        using var _ = await instrumentLocks.AcquireAsync($"{account.Id:N}|{r.Exchange}|{r.Symbol}|{r.Product}", ct);
        if (guard.Check(account) is { } refused) return Refusal(account, refused);

        int current;
        try
        {
            current = await NetQuantityAsync(account, r.Symbol, r.Exchange, r.Product, ct);
        }
        catch (Exception ex) when (ex is BrokerException or InvalidOperationException or HttpRequestException)
        {
            // OpenAlgo's refuse_smart_order_on_read_failure: never trade on a position that could not be read.
            logger.LogWarning(ex, "Smart order refused: positions of {AccountId} could not be read.", account.Id);
            return Mark(account, V1Result.Error(503, $"Your open position in {r.Symbol} could not be read from {account.Broker}, so no order was placed. Try again in a moment."));
        }

        var target = (int)(r.PositionSize ?? 0);
        string action;
        int quantity;
        if (target == 0 && current == 0) (action, quantity) = (r.Action.ToUpperInvariant(), (int)r.Quantity);
        else if (target == 0) (action, quantity) = (current > 0 ? "SELL" : "BUY", Math.Abs(current));
        else if (current == 0) (action, quantity) = (target > 0 ? "BUY" : "SELL", Math.Abs(target));
        else if (target > current) (action, quantity) = ("BUY", target - current);
        else if (target < current) (action, quantity) = ("SELL", current - target);
        else (action, quantity) = ("", 0);

        if (quantity <= 0)
            return Mark(account, new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." }));

        var result = await dispatcher.DispatchAsync(Intent(account, r.Strategy, r.Symbol, r.Exchange, action, quantity, r.PriceType, r.Product, r.Price, r.TriggerPrice, r.DisclosedQuantity), ct);
        return OrderAnswer(account, result);
    }

    public async Task<V1Result> ModifyOrderAsync(BrokerAccount account, ModifyOrderRequest r, CancellationToken ct)
    {
        if (Fractional(account, r.Quantity ?? 0) is { } fractional) return fractional;
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        if (account.KillSwitchEngaged) return Refusal(account, new Refusal("kill_switch_engaged", $"The kill switch is on for {account.DisplayName}, so orders cannot be changed. Release it first."));
        if (account.AccountType == AccountType.PAPER)
            return Mark(account, V1Result.Error(400, "Changing a sandbox order is not supported yet. Cancel it and place a new one."));
        if (!Broker<IBrokerOrders>(account, out var orders, out var session, out var missing)) return missing!;

        var change = new BrokerOrderChange(r.OrderId, r.Symbol, r.Exchange, r.Action.ToUpperInvariant(), (int)(r.Quantity ?? 0), r.PriceType, r.Product,
            r.Price ?? 0, r.TriggerPrice ?? 0, r.DisclosedQuantity ?? 0);
        return await Guarded(async () =>
        {
            var result = await orders!.ModifyAsync(session!, change, ct);
            return result.Ok
                ? new V1Result(200, new JsonObject { ["status"] = "success", ["orderid"] = r.OrderId })
                : V1Result.Error(result.HttpStatus == 200 ? 500 : result.HttpStatus, result.Message);
        }, "Failed to modify order due to internal error");
    }

    public async Task<V1Result> CancelOrderAsync(BrokerAccount account, CancelOrderRequest r, CancellationToken ct)
    {
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        if (account.AccountType == AccountType.PAPER)
        {
            return await sandbox.CancelAsync(account.Id, r.OrderId, ct)
                ? Mark(account, new V1Result(200, new JsonObject { ["status"] = "success", ["orderid"] = r.OrderId }))
                : Mark(account, V1Result.Error(400, $"Order {r.OrderId} is not open, so it cannot be cancelled."));
        }

        if (!Broker<IBrokerOrders>(account, out var orders, out var session, out var missing)) return missing!;
        return await Guarded(async () =>
        {
            var result = await orders!.CancelAsync(session!, r.OrderId, ct);
            return result.Ok
                ? new V1Result(200, new JsonObject { ["status"] = "success", ["orderid"] = r.OrderId })
                : V1Result.Error(result.HttpStatus == 200 ? 500 : result.HttpStatus, result.Message);
        }, "Failed to cancel order due to internal error");
    }

    public async Task<V1Result> CancelAllOrdersAsync(BrokerAccount account, CancelAllOrderRequest r, CancellationToken ct)
    {
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        var cancelled = new JsonArray();
        var failed = new JsonArray();
        if (account.AccountType == AccountType.PAPER)
        {
            foreach (var id in await sandbox.CancelAllAsync(account.Id, ct)) cancelled.Add(id);
        }
        else
        {
            if (!Broker<IBrokerPortfolio>(account, out var portfolio, out var session, out var missing)) return missing!;
            if (registry.Get<IBrokerOrders>(account.Broker) is not { } orders) return NotSupported(account, "cancelling orders");
            var answer = await GuardedSteps(async () =>
            {
                var book = await portfolio!.GetOrderBookAsync(session!, ct);
                foreach (var row in book.Rows.OfType<JsonObject>())
                {
                    if (Text(row["order_status"]) is not ("open" or "trigger pending") || Text(row["orderid"]) is not { Length: > 0 } id) continue;
                    var result = await orders.CancelAsync(session!, id, ct);
                    (result.Ok ? cancelled : failed).Add(id);
                }

                return null;
            }, "Failed to cancel all orders due to internal error");
            if (answer is not null) return answer;
        }

        return Mark(account, new V1Result(200, new JsonObject
        {
            ["status"] = "success",
            ["canceled_orders"] = cancelled,
            ["failed_cancellations"] = failed,
            ["message"] = $"Canceled {cancelled.Count} orders. Failed to cancel {failed.Count} orders.",
        }));
    }

    public async Task<V1Result> ClosePositionsAsync(BrokerAccount account, ClosePositionRequest r, CancellationToken ct)
    {
        if (guard.Check(account) is { } refused) return Refusal(account, refused);
        IReadOnlyList<OpenPosition> open;
        var failures = new List<string>();
        try
        {
            if (account.AccountType == AccountType.PAPER)
            {
                open = await sandbox.GetOpenPositionsAsync(account.Id, ct);
            }
            else
            {
                var (whole, fractional) = await LivePositionsWithFractionsAsync(account, ct);
                open = whole;
                // A fractional holding (crypto spot) cannot be sent as a whole-quantity order; truncating would leave it open unreported.
                failures.AddRange(fractional.Select(f => $"{f.Symbol} ({f.Exchange}): its quantity {f.Quantity} is fractional, which EP Trade bot cannot place yet. Close it on {account.Broker} directly"));
            }
        }
        catch (Exception ex) when (ex is BrokerException or InvalidOperationException or HttpRequestException)
        {
            logger.LogError(ex, "Close positions: positions of {AccountId} could not be read.", account.Id);
            return Mark(account, V1Result.Error(500, $"Your positions could not be read from {account.Broker}, so nothing was closed. Try again in a moment."));
        }

        foreach (var p in open)
        {
            // Squared off through the dispatcher like every other order: audited, and an exit is never refused by a loss limit.
            var result = await dispatcher.DispatchAsync(new OrderIntent
            {
                AccountId = account.Id, Source = OrderSource.Api, Symbol = p.Symbol, Exchange = p.Exchange,
                Action = p.NetQuantity > 0 ? "SELL" : "BUY", Quantity = Math.Abs(p.NetQuantity), Product = p.Product,
                PriceType = "MARKET", Strategy = "Squareoff", Actor = "api",
            }, ct);
            if (result.Status != DispatchStatus.Accepted) failures.Add($"{p.Symbol} ({p.Exchange}): {result.Message}");
        }

        if (failures.Count > 0)
            return Mark(account, V1Result.Error(500, $"{failures.Count} position(s) could not be squared off and are still open: {string.Join("; ", failures)}"));
        return Mark(account, new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = "All Open Positions Squared Off" }));
    }

    // ---- books -----------------------------------------------------------------------------------------------

    public async Task<V1Result> OrderBookAsync(BrokerAccount account, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER)
        {
            var orders = await sandbox.GetOrdersAsync(account.Id, ct);
            var rows = new JsonArray();
            foreach (var o in orders)
            {
                rows.Add(new JsonObject
                {
                    ["orderid"] = o.Orderid, ["symbol"] = o.Symbol, ["exchange"] = o.Exchange, ["action"] = o.Action, ["quantity"] = o.Quantity,
                    ["price"] = (double)(o.Price ?? 0), ["trigger_price"] = (double)(o.TriggerPrice ?? 0), ["pricetype"] = o.PriceType, ["product"] = o.Product,
                    ["order_status"] = o.OrderStatus, ["average_price"] = (double)(o.AveragePrice ?? 0), ["filled_quantity"] = o.FilledQuantity ?? 0,
                    ["pending_quantity"] = o.PendingQuantity, ["rejection_reason"] = o.RejectionReason ?? "",
                    ["timestamp"] = o.OrderTimestamp.ToString("yyyy-MM-dd HH:mm:ss"), ["strategy"] = o.Strategy ?? "",
                });
            }

            var stats = new JsonObject
            {
                ["total_buy_orders"] = orders.Count(o => o.Action == "BUY"), ["total_sell_orders"] = orders.Count(o => o.Action == "SELL"),
                ["total_completed_orders"] = orders.Count(o => o.OrderStatus == "complete"), ["total_open_orders"] = orders.Count(o => o.OrderStatus == "open"),
                ["total_rejected_orders"] = orders.Count(o => o.OrderStatus == "rejected"),
            };
            return Mark(account, Success(new JsonObject { ["orders"] = rows, ["statistics"] = stats }));
        }

        if (!Broker<IBrokerPortfolio>(account, out var portfolio, out var session, out var missing)) return missing!;
        return await Guarded(async () =>
        {
            var book = await portfolio!.GetOrderBookAsync(session!, ct);
            return Success(new JsonObject { ["orders"] = BookFormatting.Orders(book.Rows), ["statistics"] = BookFormatting.OrderStatistics(book.Statistics) });
        });
    }

    public async Task<V1Result> TradeBookAsync(BrokerAccount account, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER)
        {
            var rows = new JsonArray();
            foreach (var t in await sandbox.GetTradesAsync(account.Id, ct))
            {
                rows.Add(new JsonObject
                {
                    ["symbol"] = t.Symbol, ["exchange"] = t.Exchange, ["product"] = t.Product, ["action"] = t.Action, ["quantity"] = t.Quantity,
                    ["average_price"] = (double)t.Price, ["trade_value"] = BookFormatting.Round2((double)(t.Price * t.Quantity)),
                    ["orderid"] = t.Orderid, ["tradeid"] = t.Tradeid, ["timestamp"] = t.TradeTimestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                });
            }

            return Mark(account, Success(rows));
        }

        if (!Broker<IBrokerPortfolio>(account, out var portfolio, out var session, out var missing)) return missing!;
        return await Guarded(async () => Success(BookFormatting.Trades(await portfolio!.GetTradeBookAsync(session!, ct))));
    }

    public async Task<V1Result> PositionBookAsync(BrokerAccount account, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER)
        {
            var rows = new JsonArray();
            foreach (var p in await sandbox.GetPositionsAsync(account.Id, ct))
            {
                rows.Add(new JsonObject
                {
                    ["symbol"] = p.Symbol, ["exchange"] = p.Exchange, ["product"] = p.Product, ["quantity"] = p.Quantity,
                    ["average_price"] = BookFormatting.Fixed2((double)p.AveragePrice), ["ltp"] = BookFormatting.Round2((double)(p.Ltp ?? 0)),
                    ["pnl"] = BookFormatting.Round2((double)(p.Pnl ?? 0)),
                });
            }

            return Mark(account, Success(rows));
        }

        if (!Broker<IBrokerPortfolio>(account, out var portfolio, out var session, out var missing)) return missing!;
        return await Guarded(async () => Success(BookFormatting.Positions(await portfolio!.GetPositionsAsync(session!, ct))));
    }

    public async Task<V1Result> HoldingsAsync(BrokerAccount account, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER)
        {
            // Sandbox CNC holdings and T+1 settlement arrive in TG4; until then the sandbox holds none.
            return Mark(account, Success(new JsonObject
            {
                ["holdings"] = new JsonArray(),
                ["statistics"] = new JsonObject { ["totalholdingvalue"] = 0.0, ["totalinvvalue"] = 0.0, ["totalprofitandloss"] = 0.0, ["totalpnlpercentage"] = 0.0 },
            }));
        }

        if (!Broker<IBrokerPortfolio>(account, out var portfolio, out var session, out var missing)) return missing!;
        return await Guarded(async () =>
        {
            var book = await portfolio!.GetHoldingsAsync(session!, ct);
            return Success(new JsonObject { ["holdings"] = BookFormatting.Holdings(book.Rows), ["statistics"] = BookFormatting.HoldingsStatistics(book.Statistics) });
        });
    }

    public async Task<V1Result> FundsAsync(BrokerAccount account, CancellationToken ct)
    {
        if (account.AccountType == AccountType.PAPER)
        {
            var f = await sandbox.GetFundsAsync(account.Id, ct);
            return Mark(account, Success(new JsonObject
            {
                ["availablecash"] = (double)(f.AvailableBalance ?? 0), ["collateral"] = 0.0, ["m2munrealized"] = (double)(f.UnrealizedPnl ?? 0),
                ["m2mrealized"] = (double)(f.TodayRealizedPnl ?? 0), ["total_realized_pnl"] = (double)(f.RealizedPnl ?? 0),
                ["today_realized_pnl"] = (double)(f.TodayRealizedPnl ?? 0), ["utiliseddebits"] = (double)(f.UsedMargin ?? 0),
                ["grossexposure"] = (double)(f.UsedMargin ?? 0), ["totalpnl"] = (double)(f.TotalPnl ?? 0),
                ["last_reset"] = f.LastResetDate.ToString("yyyy-MM-dd HH:mm:ss"), ["reset_count"] = f.ResetCount ?? 0,
            }));
        }

        if (!Broker<IBrokerFunds>(account, out var funds, out var session, out var missing)) return missing!;
        return await Guarded(async () => Success(await funds!.GetFundsAsync(session!, ct)));
    }

    public async Task<V1Result> OrderStatusAsync(BrokerAccount account, OrderStatusRequest r, CancellationToken ct)
    {
        var book = await OrderBookAsync(account, ct);
        if (book.Status != 200) return book;
        var orders = book.Body["data"]?["orders"] as JsonArray ?? [];
        if (orders.OfType<JsonObject>().FirstOrDefault(o => Text(o["orderid"]) == r.OrderId) is not { } found)
            return Mark(account, V1Result.Error(404, $"Order {r.OrderId} not found"));

        var order = (JsonObject)found.DeepClone();
        var average = 0.0;
        if (string.Equals(Text(order["order_status"]), "complete", StringComparison.OrdinalIgnoreCase))
        {
            var trades = await TradeBookAsync(account, ct);
            if (trades.Status == 200 && trades.Body["data"] is JsonArray rows
                && rows.OfType<JsonObject>().FirstOrDefault(t => Text(t["orderid"]) == r.OrderId) is { } trade)
                average = BookFormatting.Number(trade["average_price"]) ?? 0;
        }

        order["average_price"] = average;
        return Mark(account, Success(order));
    }

    public async Task<V1Result> OpenPositionAsync(BrokerAccount account, OpenPositionRequest r, CancellationToken ct)
    {
        var book = await PositionBookAsync(account, ct);
        if (book.Status != 200) return book;
        var match = (book.Body["data"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(p => Text(p["symbol"]) == r.Symbol && Text(p["exchange"]) == r.Exchange && Text(p["product"]) == r.Product);
        return Mark(account, new V1Result(200, new JsonObject { ["quantity"] = match?["quantity"]?.DeepClone() ?? 0, ["status"] = "success" }));
    }

    // ---- market data -----------------------------------------------------------------------------------------

    public Task<V1Result> QuotesAsync(BrokerAccount account, QuotesRequest r, CancellationToken ct) =>
        MarketDataAsync(account, (q, s) => q.GetQuoteAsync(s, r.Symbol, r.Exchange, ct));

    public Task<V1Result> DepthAsync(BrokerAccount account, DepthRequest r, CancellationToken ct) =>
        MarketDataAsync(account, (q, s) => q.GetDepthAsync(s, r.Symbol, r.Exchange, ct));

    private async Task<V1Result> MarketDataAsync(BrokerAccount account, Func<IBrokerQuotes, BrokerSession, Task<JsonObject>> read)
    {
        // Market data comes from the broker for PAPER accounts too, as OpenAlgo's analyze mode does.
        if (!Broker<IBrokerQuotes>(account, out var quotes, out var session, out var missing)) return missing!;
        return await Guarded(async () => Success(await read(quotes!, session!)));
    }

    public V1Result Ping(BrokerAccount account) =>
        Mark(account, Success(new JsonObject { ["broker"] = account.Broker, ["message"] = "pong" }));

    public V1Result Analyzer(BrokerAccount account)
    {
        var analyze = account.AccountType == AccountType.PAPER;
        return new V1Result(200, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject { ["mode"] = analyze ? "analyze" : "live", ["analyze_mode"] = analyze, ["total_logs"] = 0 },
        });
    }

    public V1Result AnalyzerToggle(BrokerAccount account) =>
        V1Result.Error(400, $"In EP Trade bot the mode belongs to the account: {account.DisplayName} is a {(account.AccountType == AccountType.PAPER ? "sandbox" : "live")} account. Use the API key of the account you want to trade.");

    // ---- helpers ---------------------------------------------------------------------------------------------

    private async Task<int> NetQuantityAsync(BrokerAccount account, string symbol, string exchange, string product, CancellationToken ct)
    {
        var positions = account.AccountType == AccountType.PAPER
            ? (await sandbox.GetPositionsAsync(account.Id, ct)).Select(p => new OpenPosition(p.Symbol, p.Exchange, p.Product, p.Quantity)).ToList()
            : (IReadOnlyList<OpenPosition>)await LivePositionsAsync(account, ct, includeFlat: true);
        return positions.FirstOrDefault(p => p.Symbol == symbol && p.Exchange == exchange && p.Product == product)?.NetQuantity ?? 0;
    }

    private async Task<(List<OpenPosition> Whole, List<(string Symbol, string Exchange, double Quantity)> Fractional)> LivePositionsWithFractionsAsync(BrokerAccount account, CancellationToken ct)
    {
        if (registry.Get<IBrokerPortfolio>(account.Broker) is not { } portfolio) throw new InvalidOperationException($"{account.Broker} is not enabled on this server.");
        var session = sessions.Create(account) ?? throw new InvalidOperationException($"{account.DisplayName} is not logged in to {account.Broker}.");
        var whole = new List<OpenPosition>();
        var fractional = new List<(string, string, double)>();
        foreach (var p in (await portfolio.GetPositionsAsync(session, ct)).OfType<JsonObject>())
        {
            var quantity = BookFormatting.Number(p["quantity"]) ?? (double.TryParse(Text(p["quantity"]), System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0);
            if (quantity == 0) continue;
            if (quantity != Math.Truncate(quantity)) fractional.Add((Text(p["symbol"]) ?? "", Text(p["exchange"]) ?? "", quantity));
            else whole.Add(new OpenPosition(Text(p["symbol"]) ?? "", Text(p["exchange"]) ?? "", Text(p["product"]) ?? "", (int)quantity));
        }

        return (whole, fractional);
    }

    private async Task<List<OpenPosition>> LivePositionsAsync(BrokerAccount account, CancellationToken ct, bool includeFlat = false)
    {
        if (registry.Get<IBrokerPortfolio>(account.Broker) is not { } portfolio) throw new InvalidOperationException($"{account.Broker} is not enabled on this server.");
        var session = sessions.Create(account) ?? throw new InvalidOperationException($"{account.DisplayName} is not logged in to {account.Broker}.");
        var rows = await portfolio.GetPositionsAsync(session, ct);
        return rows.OfType<JsonObject>()
            .Select(p => new OpenPosition(Text(p["symbol"]) ?? "", Text(p["exchange"]) ?? "", Text(p["product"]) ?? "",
                (int)(BookFormatting.Number(p["quantity"]) ?? (double.TryParse(Text(p["quantity"]), System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0))))
            .Where(p => includeFlat || p.NetQuantity != 0)
            .ToList();
    }

    private static OrderIntent Intent(BrokerAccount account, string strategy, string symbol, string exchange, string action, decimal quantity,
        string priceType, string product, decimal price, decimal trigger, int disclosed) => new()
    {
        AccountId = account.Id, Source = OrderSource.Api, Symbol = symbol, Exchange = exchange, Action = action.ToUpperInvariant(),
        Quantity = (int)quantity, PriceType = priceType, Product = product, Price = price, TriggerPrice = trigger,
        DisclosedQuantity = disclosed, Strategy = strategy, Actor = "api",
    };

    private static V1Result OrderAnswer(BrokerAccount account, DispatchResult result) => result.Status switch
    {
        DispatchStatus.Accepted => Mark(account, new V1Result(200, new JsonObject { ["status"] = "success", ["orderid"] = result.OrderId })),
        DispatchStatus.Queued => Mark(account, new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = result.Message })),
        DispatchStatus.Refused => Mark(account, V1Result.Error(result.Code is "live_locked" or "account_disabled" ? 403 : 400, result.Message)),
        _ => Mark(account, V1Result.Error(result.HttpStatus is { } s and not 200 ? s : 500, result.Message)),
    };

    private static V1Result Refusal(BrokerAccount account, Refusal refusal) =>
        Mark(account, V1Result.Error(refusal.Code is "live_locked" or "account_disabled" ? 403 : 400, refusal.Message));

    private static V1Result Success(JsonNode data) => new(200, new JsonObject { ["status"] = "success", ["data"] = data });

    /// <summary>Sandbox answers carry OpenAlgo's analyze-mode marker.</summary>
    private static V1Result Mark(BrokerAccount account, V1Result result)
    {
        if (account.AccountType == AccountType.PAPER) result.Body["mode"] = "analyze";
        return result;
    }

    private static V1Result NotSupported(BrokerAccount account, string what) =>
        V1Result.Error(501, $"{account.Broker} does not support {what} in EP Trade bot yet.");

    private bool Broker<T>(BrokerAccount account, out T? plugin, out BrokerSession? session, out V1Result? missing) where T : class, IBrokerPlugin
    {
        plugin = null;
        session = null;
        missing = null;
        if (registry.Find(account.Broker) is null)
        {
            missing = V1Result.Error(404, $"{account.Broker} is not enabled on this server.");
            return false;
        }

        plugin = registry.Get<T>(account.Broker);
        if (plugin is null)
        {
            missing = NotSupported(account, typeof(T).Name switch
            {
                nameof(IBrokerOrders) => "orders",
                nameof(IBrokerPortfolio) => "order, trade and position books",
                nameof(IBrokerFunds) => "funds",
                nameof(IBrokerQuotes) => "quotes and depth",
                _ => "this request",
            });
            return false;
        }

        session = sessions.Create(account);
        if (session is null)
        {
            missing = V1Result.Error(403, $"{account.DisplayName} is not logged in to {account.Broker}. Log in to the broker, then try again.");
            return false;
        }

        return true;
    }

    private async Task<V1Result> Guarded(Func<Task<V1Result>> call, string? internalMessage = null)
    {
        try
        {
            return await call();
        }
        catch (BrokerException ex)
        {
            logger.LogError(ex, "Broker call failed.");
            return V1Result.Error(ex.HttpStatus, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Broker did not answer.");
            return V1Result.Error(502, internalMessage ?? "The broker did not answer. Try again in a moment.");
        }
    }

    private async Task<V1Result?> GuardedSteps(Func<Task<V1Result?>> call, string internalMessage)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is BrokerException or HttpRequestException)
        {
            logger.LogError(ex, "Broker call failed.");
            return V1Result.Error(500, internalMessage);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s
        : node is JsonValue e && e.TryGetValue<System.Text.Json.JsonElement>(out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : null;
}
