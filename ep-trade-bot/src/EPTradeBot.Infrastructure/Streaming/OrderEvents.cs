using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Time;
using EPTradeBot.Core.Trading;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Streaming;

/// <summary>What happened to one order, as pushed to the web app (SignalR) and to WebSocket order subscribers.</summary>
public sealed record OrderUpdate(
    Guid AccountId,
    string Mode,
    string Source,
    string Symbol,
    string Exchange,
    string Action,
    int Quantity,
    string PriceType,
    string Product,
    string Status,
    string? OrderId,
    string Message,
    DateTime TimestampIst);

public interface IOrderUpdateSink
{
    Task OnOrderAsync(OrderUpdate update);
}

/// <summary>Fans an order outcome out to every sink. A failing sink never fails the order.</summary>
public sealed class OrderEvents(IEnumerable<IOrderUpdateSink> sinks, ILogger<OrderEvents> logger)
{
    public async Task PublishAsync(OrderIntent intent, BrokerAccount? account, DispatchResult result)
    {
        var update = new OrderUpdate(
            intent.AccountId,
            account?.AccountType == AccountType.LIVE ? "live" : "analyze",
            intent.Source.ToString(),
            intent.Symbol, intent.Exchange, intent.Action, intent.Quantity, intent.PriceType, intent.Product,
            result.Status switch
            {
                DispatchStatus.Accepted => "accepted",
                DispatchStatus.Refused => "refused",
                DispatchStatus.Queued => "queued",
                _ => "failed",
            },
            result.OrderId, result.Message, Ist.FromUtc(DateTime.UtcNow));
        foreach (var sink in sinks)
        {
            try
            {
                await sink.OnOrderAsync(update);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "An order update could not be delivered to {Sink}.", sink.GetType().Name);
            }
        }
    }
}
