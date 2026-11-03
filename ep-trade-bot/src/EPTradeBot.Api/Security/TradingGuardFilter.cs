using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Api.Security;

/// <summary>
/// The middleware half of the environment guard: an order route naming an account the
/// environment may not trade answers 403 before the handler runs. The dispatcher checks
/// again, because internal callers (strategies, Flow, the agent) never pass through here.
/// </summary>
internal sealed class TradingGuardFilter(ITradingGuardService guard, EpTradeDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!Guid.TryParse(context.HttpContext.GetRouteValue("accountId")?.ToString(), out var accountId))
            return TypedResults.Problem(title: "Account not found.", detail: "This account does not exist.", statusCode: StatusCodes.Status404NotFound);

        var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, context.HttpContext.RequestAborted);
        if (guard.Check(account) is { } refusal)
        {
            var status = refusal.Code == "account_not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden;
            return TypedResults.Problem(title: "Order not allowed.", detail: refusal.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["code"] = refusal.Code });
        }

        return await next(context);
    }
}
