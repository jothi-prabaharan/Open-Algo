using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Sandbox;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>Loads the account's limits and exposure and applies <see cref="AccountRiskRules"/>.</summary>
public sealed class AccountRiskManager(
    IDbContextFactory<EpTradeDbContext> contexts,
    SandboxEngine sandbox,
    ILiveOrderRouter live,
    IFreezeQuantityLookup freeze) : IAccountRiskManager
{
    public async Task<Refusal?> CheckAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default)
    {
        AccountRiskSetting? settings;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            settings = await db.AccountRiskSettings.AsNoTracking().SingleOrDefaultAsync(s => s.AccountId == account.Id, cancellationToken);
        }

        var freezeQuantity = await freeze.GetAsync(intent.Exchange, intent.Symbol, cancellationToken);
        AccountExposure? exposure = account.AccountType == AccountType.PAPER
            ? await sandbox.GetExposureAsync(intent, freezeQuantity, cancellationToken)
            : await live.GetExposureAsync(account, intent, cancellationToken);

        if (exposure is null)
        {
            // Fail closed: limits that cannot be computed are not limits that passed.
            return new Refusal("exposure_unavailable",
                $"The positions of {account.DisplayName} could not be read from {account.Broker}, so its risk limits cannot be checked. Nothing was sent.");
        }

        return AccountRiskRules.Check(account, settings, exposure with { FreezeQuantity = freezeQuantity }, intent);
    }
}
