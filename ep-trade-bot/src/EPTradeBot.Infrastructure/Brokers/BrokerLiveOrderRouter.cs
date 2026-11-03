using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Brokers;

/// <summary>
/// Sends LIVE orders to the account's broker plugin. Every failure is reported as a
/// failure: no plugin, no session, a refusal, or a request that may have been sent
/// without an answer. Exposure that cannot be read is null, so risk checks refuse.
/// </summary>
public sealed class BrokerLiveOrderRouter(
    IBrokerRegistry registry,
    BrokerSessionFactory sessions,
    IQuoteSource quotes,
    ILogger<BrokerLiveOrderRouter> logger) : ILiveOrderRouter
{
    public async Task<DispatchResult> PlaceAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default)
    {
        if (registry.Get<IBrokerOrders>(account.Broker) is not { } orders) return NoPlugin(account);
        if (sessions.Create(account) is not { } session) return NotLoggedIn(account);

        var order = new BrokerOrder(intent.Symbol, intent.Exchange, intent.Action.ToUpperInvariant(), intent.Quantity, intent.PriceType,
            intent.Product, intent.Price, intent.TriggerPrice, intent.DisclosedQuantity, intent.Strategy);
        try
        {
            var result = await orders.PlaceAsync(session, order, cancellationToken);
            return result.Ok
                ? new DispatchResult(DispatchStatus.Accepted, "Order placed.", result.OrderId)
                : new DispatchResult(DispatchStatus.Failed, result.Message, Code: result.MayHaveBeenSent ? "broker_no_answer" : "broker_refused", HttpStatus: result.HttpStatus);
        }
        catch (BrokerException ex)
        {
            logger.LogError(ex, "{Broker} order for account {AccountId} failed.", account.Broker, account.Id);
            return new DispatchResult(DispatchStatus.Failed, ex.Message, Code: "broker_error", HttpStatus: ex.HttpStatus);
        }
    }

    public async Task<AccountExposure?> GetExposureAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default)
    {
        if (registry.Get<IBrokerPortfolio>(account.Broker) is not { } portfolio || sessions.Create(account) is not { } session) return null;
        JsonArray positions;
        try
        {
            positions = await portfolio.GetPositionsAsync(session, cancellationToken);
        }
        catch (Exception ex) when (ex is BrokerException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not read positions of account {AccountId} for the risk check.", account.Id);
            return null;
        }

        decimal dayPnl = 0, gross = 0;
        decimal? positionLtp = null;
        var net = 0;
        foreach (var row in positions.OfType<JsonObject>())
        {
            var quantity = (decimal)(BookFormatting.Number(row["quantity"]) ?? Parse(row["quantity"]));
            var ltp = (decimal)(BookFormatting.Number(row["ltp"]) ?? 0);
            dayPnl += (decimal)(BookFormatting.Number(row["pnl"]) ?? 0);
            if (Text(row["symbol"]) == intent.Symbol && Text(row["exchange"]) == intent.Exchange)
            {
                if (ltp > 0) positionLtp = ltp;
                if (Text(row["product"]) == intent.Product) net = (int)quantity;
            }

            if (quantity == 0) continue;
            var mark = quotes.Get(Text(row["exchange"]) ?? "", Text(row["symbol"]) ?? "")?.Ltp ?? ltp;
            gross += Math.Abs(quantity) * mark;
        }

        var price = quotes.Get(intent.Exchange, intent.Symbol)?.Ltp ?? positionLtp;
        if (price is null && registry.Get<IBrokerQuotes>(account.Broker) is { } quoteSource)
        {
            try
            {
                var quote = await quoteSource.GetQuoteAsync(session, intent.Symbol, intent.Exchange, cancellationToken);
                if (BookFormatting.Number(quote["ltp"]) is { } ltp && ltp > 0) price = (decimal)ltp;
            }
            catch (Exception ex) when (ex is BrokerException or HttpRequestException)
            {
                logger.LogWarning(ex, "Could not read a price for {Exchange}:{Symbol}.", intent.Exchange, intent.Symbol);
            }
        }

        // Positions report the broker's P&L for the positions it holds, which is what the daily loss limit reads.
        return new AccountExposure { DayPnl = dayPnl, GrossOpenValue = gross, NetQuantityInInstrument = net, MarkPrice = price };
    }

    public async Task<IReadOnlyList<string>> CancelAllAsync(BrokerAccount account, CancellationToken cancellationToken = default)
    {
        var (portfolio, orders, session) = Require(account);
        var book = await portfolio.GetOrderBookAsync(session, cancellationToken);
        var cancelled = new List<string>();
        var failed = new List<string>();
        foreach (var row in book.Rows.OfType<JsonObject>())
        {
            if (Text(row["order_status"]) is not ("open" or "trigger pending") || Text(row["orderid"]) is not { Length: > 0 } id) continue;
            var result = await orders.CancelAsync(session, id, cancellationToken);
            (result.Ok ? cancelled : failed).Add(id);
        }

        if (failed.Count > 0)
            throw new InvalidOperationException($"{failed.Count} open order(s) at {account.Broker} could not be cancelled: {string.Join(", ", failed)}.");
        return cancelled;
    }

    public async Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(BrokerAccount account, CancellationToken cancellationToken = default)
    {
        var (portfolio, _, session) = Require(account);
        var positions = await portfolio.GetPositionsAsync(session, cancellationToken);
        return positions.OfType<JsonObject>()
            .Select(p => new OpenPosition(Text(p["symbol"]) ?? "", Text(p["exchange"]) ?? "", Text(p["product"]) ?? "",
                (int)(BookFormatting.Number(p["quantity"]) ?? Parse(p["quantity"]))))
            .Where(p => p.NetQuantity != 0)
            .ToList();
    }

    private (IBrokerPortfolio, IBrokerOrders, BrokerSession) Require(BrokerAccount account)
    {
        var portfolio = registry.Get<IBrokerPortfolio>(account.Broker);
        var orders = registry.Get<IBrokerOrders>(account.Broker);
        if (portfolio is null || orders is null) throw new InvalidOperationException(NoPlugin(account).Message);
        var session = sessions.Create(account) ?? throw new InvalidOperationException(NotLoggedIn(account).Message);
        return (portfolio, orders, session);
    }

    private static DispatchResult NoPlugin(BrokerAccount account) => new(DispatchStatus.Failed,
        $"{account.Broker} is not enabled on this server, so orders for {account.DisplayName} cannot be sent. Nothing was sent.", Code: "broker_not_enabled", HttpStatus: 404);

    private static DispatchResult NotLoggedIn(BrokerAccount account) => new(DispatchStatus.Failed,
        $"{account.DisplayName} is not logged in to {account.Broker}. Log in to the broker, then try again. Nothing was sent.", Code: "broker_not_logged_in", HttpStatus: 403);

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s
        : node is JsonValue e && e.TryGetValue<System.Text.Json.JsonElement>(out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : null;

    private static double Parse(JsonNode? node) =>
        double.TryParse(Text(node), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
}
