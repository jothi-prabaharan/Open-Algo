using EPTradeBot.Api.Security;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Contracts.Management;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Api.Endpoints;

public sealed record RiskSettingsView(decimal MaxDailyLoss, int MaxOrderQuantity, decimal MaxPositionValue, bool IsEnabled, DateTime UpdatedAtUtc);

public sealed record AccountView(
    Guid Id, string DisplayName, string Broker, string? ClientId, string AccountType, bool IsEnabled,
    bool Locked, string? LockedReason, bool KillSwitchEngaged, DateTime? KillSwitchEngagedAtUtc, string? KillSwitchReason, RiskSettingsView? Risk);

internal static class AccountEndpoints
{
    public static RouteGroupBuilder MapManagement(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("").AddEndpointFilter<AdminTokenFilter>().WithDataAnnotations();

        admin.MapGet("/accounts", async (EpTradeDbContext db, ITradingGuardService guard, CancellationToken ct) =>
        {
            var accounts = await db.BrokerAccounts.AsNoTracking().Include(a => a.RiskSetting).OrderBy(a => a.DisplayName).ToListAsync(ct);
            return Results.Ok(accounts.Select(a => View(a, guard)));
        });

        admin.MapGet("/accounts/{accountId:guid}", async (Guid accountId, EpTradeDbContext db, ITradingGuardService guard, CancellationToken ct) =>
            await db.BrokerAccounts.AsNoTracking().Include(a => a.RiskSetting).SingleOrDefaultAsync(a => a.Id == accountId, ct) is { } a
                ? Results.Ok(View(a, guard))
                : NotFound());

        admin.MapPost("/accounts", async (CreateBrokerAccountRequest request, EpTradeDbContext db, ITradingGuardService guard, CancellationToken ct) =>
        {
            var account = new BrokerAccount
            {
                DisplayName = request.DisplayName.Trim(),
                Broker = request.Broker,
                ClientId = string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim(),
                AccountType = Enum.Parse<AccountType>(request.AccountType!),
            };
            db.BrokerAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/accounts/{account.Id}", View(account, guard));
        });

        admin.MapGet("/accounts/{accountId:guid}/risk", async (Guid accountId, EpTradeDbContext db, CancellationToken ct) =>
            await db.AccountRiskSettings.AsNoTracking().SingleOrDefaultAsync(r => r.AccountId == accountId, ct) is { } r
                ? Results.Ok(new RiskSettingsView(r.MaxDailyLoss, r.MaxOrderQuantity, r.MaxPositionValue, r.IsEnabled, r.UpdatedAtUtc))
                : NotFound("No risk limits are set for this account yet."));

        admin.MapPut("/accounts/{accountId:guid}/risk", async (Guid accountId, UpdateAccountRiskSettingsRequest request, EpTradeDbContext db, CancellationToken ct) =>
        {
            if (!await db.BrokerAccounts.AnyAsync(a => a.Id == accountId, ct)) return NotFound();
            var r = await db.AccountRiskSettings.SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
            if (r is null)
            {
                r = new AccountRiskSetting { AccountId = accountId };
                db.AccountRiskSettings.Add(r);
            }

            r.MaxDailyLoss = request.MaxDailyLoss!.Value;
            r.MaxOrderQuantity = request.MaxOrderQuantity!.Value;
            r.MaxPositionValue = request.MaxPositionValue!.Value;
            r.IsEnabled = request.IsEnabled;
            r.UpdatedAtUtc = DateTime.UtcNow;
            r.UpdatedBy = "admin";
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RiskSettingsView(r.MaxDailyLoss, r.MaxOrderQuantity, r.MaxPositionValue, r.IsEnabled, r.UpdatedAtUtc));
        });

        // Order routes carry the guard filter: a LIVE account in a non-Production environment is refused here (403)
        // and again inside the dispatcher.
        var orders = admin.MapGroup("/accounts/{accountId:guid}").AddEndpointFilter<TradingGuardFilter>();

        orders.MapPost("/orders", async (Guid accountId, ManualOrderRequest request, IOrderDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.DispatchAsync(new OrderIntent
            {
                AccountId = accountId, Source = OrderSource.Manual, Symbol = request.Symbol, Exchange = request.Exchange,
                Action = request.Action, Quantity = request.Quantity, Product = request.Product, PriceType = request.PriceType,
                Price = request.Price, TriggerPrice = request.TriggerPrice, Strategy = request.Strategy, Actor = "admin",
            }, ct);
            return ToHttp(result);
        });

        admin.MapDelete("/accounts/{accountId:guid}/orders/{orderId}", async (Guid accountId, string orderId, SandboxEngine sandbox, CancellationToken ct) =>
            await sandbox.CancelAsync(accountId, orderId, ct)
                ? Results.Ok(new { status = "cancelled", order_id = orderId })
                : NotFound("No open order with this id."));

        admin.MapGet("/accounts/{accountId:guid}/orders", async (Guid accountId, SandboxEngine sandbox, CancellationToken ct) => Results.Ok(await sandbox.GetOrdersAsync(accountId, ct)));
        admin.MapGet("/accounts/{accountId:guid}/positions", async (Guid accountId, SandboxEngine sandbox, CancellationToken ct) => Results.Ok(await sandbox.GetPositionsAsync(accountId, ct)));
        admin.MapGet("/accounts/{accountId:guid}/funds", async (Guid accountId, SandboxEngine sandbox, CancellationToken ct) => Results.Ok(await sandbox.GetFundsAsync(accountId, ct)));

        admin.MapPost("/killswitch/account/{accountId:guid}", async (Guid accountId, AccountKillSwitchRequest request, KillSwitchService kill, CancellationToken ct) =>
            await kill.SetAccountAsync(accountId, request.Engage!.Value, "admin", request.Reason, ct) is { } result
                ? Results.Ok(result)
                : NotFound());

        admin.MapPost("/killswitch/global", async (GlobalKillSwitchRequest request, KillSwitchService kill, CancellationToken ct) =>
            Results.Ok(await kill.EngageGlobalAsync("admin", request.Reason, ct)));

        return v1;
    }

    /// <summary>Development only: sets a quote and matches open sandbox orders against it, until the market feed arrives in TG3.</summary>
    public static RouteGroupBuilder MapDevelopmentQuotes(this RouteGroupBuilder v1)
    {
        v1.MapGroup("").AddEndpointFilter<AdminTokenFilter>().WithDataAnnotations()
            .MapPost("/sandbox/quotes", async (SetQuoteRequest request, QuoteBook book, SandboxEngine sandbox, CancellationToken ct) =>
            {
                book.Set(request.Exchange, request.Symbol, new Quote(request.Ltp, request.Bid, request.Ask));
                var filled = await sandbox.MatchAsync(request.Exchange, request.Symbol, ct);
                return Results.Ok(new { filled });
            });
        return v1;
    }

    private static AccountView View(BrokerAccount a, ITradingGuardService guard)
    {
        var locked = TradingGuard.IsLocked(a, guard.EnvironmentName);
        var r = a.RiskSetting;
        return new AccountView(a.Id, a.DisplayName, a.Broker, a.ClientId, a.AccountType.ToString(), a.IsEnabled,
            locked, locked ? $"Live accounts are locked in the {guard.EnvironmentName} environment." : null,
            a.KillSwitchEngaged, a.KillSwitchEngagedAtUtc, a.KillSwitchReason,
            r is null ? null : new RiskSettingsView(r.MaxDailyLoss, r.MaxOrderQuantity, r.MaxPositionValue, r.IsEnabled, r.UpdatedAtUtc));
    }

    private static IResult ToHttp(DispatchResult result) => result.Status switch
    {
        DispatchStatus.Accepted or DispatchStatus.Queued => Results.Ok(new { status = result.Status.ToString().ToLowerInvariant(), message = result.Message, order_id = result.OrderId }),
        DispatchStatus.Refused => Results.Problem(title: "Order refused.", detail: result.Message, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
        _ => Results.Problem(title: "Order failed.", detail: result.Message, statusCode: StatusCodes.Status502BadGateway,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
    };

    private static IResult NotFound(string detail = "This account does not exist.") =>
        Results.Problem(title: "Not found.", detail: detail, statusCode: StatusCodes.Status404NotFound);
}
