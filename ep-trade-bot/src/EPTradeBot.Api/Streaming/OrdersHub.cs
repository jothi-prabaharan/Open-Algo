using EPTradeBot.Api.Security;
using EPTradeBot.Infrastructure.Streaming;
using Microsoft.AspNetCore.SignalR;

namespace EPTradeBot.Api.Streaming;

/// <summary>
/// Order updates for the web app. Until login arrives (TG5) a connection must carry the
/// admin token (<c>access_token</c> query value, as SignalR clients send it); one
/// without it is closed. Every update goes to the "orders" group; the app filters by account.
/// </summary>
public sealed class OrdersHub(AdminTokenCheck adminToken) : Hub
{
    public const string Path = "/hubs/orders";
    public const string Group = "orders";

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
        if (!adminToken.Matches(token))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group);
        await base.OnConnectedAsync();
    }
}

/// <summary>Sends each order update to the hub's group.</summary>
public sealed class SignalROrderSink(IHubContext<OrdersHub> hub) : IOrderUpdateSink
{
    public Task OnOrderAsync(OrderUpdate update) => hub.Clients.Group(OrdersHub.Group).SendAsync("order_update", update);
}
