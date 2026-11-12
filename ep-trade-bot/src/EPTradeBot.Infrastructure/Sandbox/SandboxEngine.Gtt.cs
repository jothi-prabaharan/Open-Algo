using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Core.Sandbox;
using EPTradeBot.Core.Time;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Sandbox;

/// <summary>What a GTT request asks for. SINGLE has one trigger; OCO has a stoploss trigger below and a target trigger above, and the first to be reached cancels the other.</summary>
public sealed record GttPlacement(string TriggerType, string Symbol, string Exchange, string Action, string Product, int Quantity, string PriceType, decimal Price,
    decimal TriggerPriceSl, decimal TriggerPriceTg, decimal StopLoss, decimal Target, string? Strategy, string? ExpiresAt);

public sealed record GttResult(bool Ok, int Code, string? TriggerId = null, string? Message = null);

/// <summary>
/// Good-till-triggered orders in the sandbox, ported from sandbox/gtt_manager.py. Placing one reserves the margin its order would need (the larger leg for an OCO); when the price reaches a
/// trigger the reservation is released and the order is placed, and an OCO's other leg is cancelled. A GTT that is cancelled, expires or fires gives its margin back exactly once.
/// </summary>
public sealed partial class SandboxEngine
{
    public const int DefaultGttExpiryDays = 365;

    private static string NewGttId() => $"GTT-{DateTime.UtcNow.AddHours(5.5):yyMMdd}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

    private sealed record Leg(decimal Trigger, string Direction, string Action, int Quantity, decimal Price, string PriceType);

    /// <summary>The trigger levels as legs, or none when the request does not describe a valid SINGLE or OCO.</summary>
    private static List<Leg> BuildLegs(GttPlacement p)
    {
        var action = p.Action.ToUpperInvariant();
        var priceType = string.IsNullOrEmpty(p.PriceType) ? "LIMIT" : p.PriceType.ToUpperInvariant();
        if (string.Equals(p.TriggerType, "OCO", StringComparison.OrdinalIgnoreCase))
        {
            if (p.TriggerPriceSl <= 0 || p.TriggerPriceTg <= 0) return [];
            return
            [
                new Leg(p.TriggerPriceSl, "below", action, p.Quantity, p.StopLoss != 0 ? p.StopLoss : p.Price, priceType),
                new Leg(p.TriggerPriceTg, "above", action, p.Quantity, p.Target != 0 ? p.Target : p.Price, priceType),
            ];
        }

        var trigger = p.TriggerPriceSl != 0 ? p.TriggerPriceSl : p.TriggerPriceTg;
        if (trigger <= 0 || (p.TriggerPriceSl > 0 && p.TriggerPriceTg > 0)) return [];
        return [new Leg(trigger, p.TriggerPriceSl > 0 ? "below" : "above", action, p.Quantity, p.Price, priceType)];
    }

    /// <summary>A MARKET leg has no limit price, so it is sized at its trigger, else at the price when the GTT was placed.</summary>
    private static decimal PricingBasis(Leg leg, decimal lastPrice) => leg.Price > 0 ? leg.Price : leg.Trigger > 0 ? leg.Trigger : lastPrice;

    /// <summary>Whether the price has crossed a trigger in its direction; touching it counts.</summary>
    public static bool IsTriggeredBy(string direction, decimal trigger, decimal ltp) => string.Equals(direction, "above", StringComparison.OrdinalIgnoreCase) ? ltp >= trigger : ltp <= trigger;

    private DateTime ResolveExpiry(string? supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            foreach (var format in new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" })
                if (DateTime.TryParseExact(supplied, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return Ist.ToUtc(exact);
            if (DateTimeOffset.TryParse(supplied, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed.UtcDateTime;
        }

        return UtcNow.AddDays(DefaultGttExpiryDays);
    }

    private decimal Blocked(string triggerType, IEnumerable<decimal> margins) => triggerType == "two-leg" ? margins.Max() : margins.Sum();

    private static string Insufficient(decimal needed, decimal? available) => $"Not enough sandbox funds: this GTT needs INR {needed:N2} of margin and INR {available ?? 0:N2} is available.";

    public Task<GttResult> PlaceGttAsync(Guid accountId, GttPlacement p, decimal lastPrice, CancellationToken ct = default) =>
        WithRetryAsync(async db =>
        {
            var legs = BuildLegs(p);
            if (legs.Count == 0) return new GttResult(false, 400, Message: "A SINGLE GTT needs exactly one of triggerprice_sl or triggerprice_tg; an OCO needs both.");
            var margins = new List<decimal>();
            foreach (var leg in legs)
            {
                var basis = PricingBasis(leg, lastPrice);
                if (basis <= 0) return new GttResult(false, 400, Message: $"There is no price for {p.Symbol} yet, so the sandbox cannot work out the margin. Try again once the price is showing.");
                margins.Add(SandboxRules.Margin(_leverage, p.Exchange, p.Product, p.Symbol, leg.Action == "BUY", leg.Quantity, basis));
            }

            var triggerType = legs.Count == 2 ? "two-leg" : "single";
            var blocked = Blocked(triggerType, margins);
            var userId = UserId(accountId);
            var funds = await LoadFundsAsync(db, userId, ct);
            if (blocked > (funds.AvailableBalance ?? 0)) return new GttResult(false, 400, Message: Insufficient(blocked, funds.AvailableBalance));
            funds.AvailableBalance -= blocked;
            funds.UsedMargin += blocked;
            funds.UpdatedAt = UtcNow;

            var id = NewGttId();
            db.Add(new SandboxGtt
            {
                GttId = id, UserId = userId, Strategy = p.Strategy, TriggerType = triggerType, Symbol = p.Symbol, Exchange = p.Exchange, LastPrice = lastPrice, GttStatus = "active",
                MarginBlocked = blocked, ExpiresAt = ResolveExpiry(p.ExpiresAt), CreatedAt = UtcNow, UpdatedAt = UtcNow,
            });
            for (var i = 0; i < legs.Count; i++)
                db.Add(new SandboxGttLeg
                {
                    GttId = id, LegNumber = i + 1, TriggerPrice = legs[i].Trigger, TriggerDirection = legs[i].Direction, Action = legs[i].Action, Quantity = legs[i].Quantity,
                    Price = legs[i].Price, Pricetype = legs[i].PriceType, Product = p.Product, LegStatus = "pending", LegMargin = margins[i], CreatedAt = UtcNow, UpdatedAt = UtcNow,
                });
            await db.SaveChangesAsync(ct);
            return new GttResult(true, 200, id);
        }, ct);

    public Task<GttResult> ModifyGttAsync(Guid accountId, string triggerId, GttPlacement p, CancellationToken ct = default) =>
        WithRetryAsync(async db =>
        {
            var userId = UserId(accountId);
            var gtt = await db.Set<SandboxGtt>().SingleOrDefaultAsync(g => g.GttId == triggerId && g.UserId == userId && g.GttStatus == "active", ct);
            if (gtt is null) return new GttResult(false, 404, Message: $"No active GTT with trigger_id '{triggerId}'");
            var existing = await db.Set<SandboxGttLeg>().Where(l => l.GttId == triggerId).OrderBy(l => l.LegNumber).ToListAsync(ct);

            if (!string.IsNullOrEmpty(p.Action) && !string.Equals(p.Action, existing[0].Action, StringComparison.OrdinalIgnoreCase))
                return new GttResult(false, 400, Message: $"action cannot be changed on an existing GTT ({existing[0].Action} -> {p.Action.ToUpperInvariant()}). Cancel and re-place.");
            if (!string.IsNullOrEmpty(p.Symbol) && !string.Equals(p.Symbol, gtt.Symbol, StringComparison.OrdinalIgnoreCase))
                return new GttResult(false, 400, Message: $"symbol cannot be changed on an existing GTT ({gtt.Symbol} -> {p.Symbol.ToUpperInvariant()}). Cancel and re-place.");
            if (!string.IsNullOrEmpty(p.Exchange) && !string.Equals(p.Exchange, gtt.Exchange, StringComparison.OrdinalIgnoreCase))
                return new GttResult(false, 400, Message: $"exchange cannot be changed on an existing GTT ({gtt.Exchange} -> {p.Exchange.ToUpperInvariant()}). Cancel and re-place.");
            var current = gtt.TriggerType == "two-leg" ? "OCO" : "SINGLE";
            if (!string.IsNullOrEmpty(p.TriggerType) && !string.Equals(p.TriggerType, current, StringComparison.OrdinalIgnoreCase))
                return new GttResult(false, 400, Message: $"trigger_type cannot be changed ({current} -> {p.TriggerType.ToUpperInvariant()}). Cancel and re-place.");

            var legs = BuildLegs(p with { Action = existing[0].Action });
            if (legs.Count == 0 || legs.Count != existing.Count) return new GttResult(false, 400, Message: $"Modify must keep the same trigger shape: this GTT has {existing.Count} leg(s).");
            var product = string.IsNullOrEmpty(p.Product) ? existing[0].Product : p.Product;
            var margins = new List<decimal>();
            foreach (var leg in legs)
            {
                var basis = PricingBasis(leg, gtt.LastPrice);
                if (basis <= 0) return new GttResult(false, 400, Message: $"There is no price for {gtt.Symbol} yet, so the sandbox cannot work out the margin.");
                margins.Add(SandboxRules.Margin(_leverage, gtt.Exchange, product, gtt.Symbol, leg.Action == "BUY", leg.Quantity, basis));
            }

            var newBlocked = Blocked(gtt.TriggerType, margins);
            var delta = newBlocked - gtt.MarginBlocked;
            var funds = await LoadFundsAsync(db, userId, ct);
            if (delta > (funds.AvailableBalance ?? 0)) return new GttResult(false, 400, triggerId, $"{Insufficient(newBlocked, funds.AvailableBalance)} The GTT is unchanged.");
            funds.AvailableBalance -= delta;
            funds.UsedMargin += delta;
            funds.UpdatedAt = UtcNow;
            gtt.MarginBlocked = newBlocked;
            gtt.UpdatedAt = UtcNow;
            for (var i = 0; i < existing.Count; i++)
            {
                existing[i].TriggerPrice = legs[i].Trigger;
                existing[i].TriggerDirection = legs[i].Direction;
                existing[i].Quantity = legs[i].Quantity;
                existing[i].Price = legs[i].Price;
                existing[i].Pricetype = legs[i].PriceType;
                existing[i].Product = product;
                existing[i].LegMargin = margins[i];
                existing[i].UpdatedAt = UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return new GttResult(true, 200, triggerId);
        }, ct);

    /// <summary>Gives a GTT's margin back to the account, never below zero reserved.</summary>
    private async Task ReleaseGttMarginAsync(EpTradeDbContext db, string userId, decimal amount, CancellationToken ct)
    {
        if (amount <= 0) return;
        var funds = await LoadFundsAsync(db, userId, ct);
        funds.AvailableBalance += amount;
        funds.UsedMargin = Math.Max(0m, (funds.UsedMargin ?? 0m) - amount);
        funds.UpdatedAt = UtcNow;
    }

    public Task<GttResult> CancelGttAsync(Guid accountId, string triggerId, CancellationToken ct = default) =>
        WithRetryAsync(async db =>
        {
            var userId = UserId(accountId);
            var gtt = await db.Set<SandboxGtt>().AsNoTracking().SingleOrDefaultAsync(g => g.GttId == triggerId && g.UserId == userId && g.GttStatus == "active", ct);
            if (gtt is null) return new GttResult(false, 404, Message: $"No active GTT with trigger_id '{triggerId}'");
            // Only the caller whose update matched may touch the legs or the funds: a trigger firing at the same moment would otherwise be cancelled after its order was placed.
            var won = await db.Set<SandboxGtt>().Where(g => g.GttId == triggerId && g.UserId == userId && g.GttStatus == "active")
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.GttStatus, "cancelled").SetProperty(g => g.MarginBlocked, 0m).SetProperty(g => g.UpdatedAt, UtcNow), ct);
            if (won != 1) return new GttResult(false, 404, Message: $"No active GTT with trigger_id '{triggerId}'");
            await db.Set<SandboxGttLeg>().Where(l => l.GttId == triggerId && (l.LegStatus == "pending" || l.LegStatus == "triggering"))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.LegStatus, "cancelled").SetProperty(l => l.ClaimedAt, (DateTime?)null).SetProperty(l => l.UpdatedAt, UtcNow), ct);
            await ReleaseGttMarginAsync(db, userId, gtt.MarginBlocked, ct);
            await db.SaveChangesAsync(ct);
            return new GttResult(true, 200, triggerId);
        }, ct);

    public async Task<JsonArray> ListGttsAsync(Guid accountId, bool activeOnly, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = UserId(accountId);
        var query = db.Set<SandboxGtt>().AsNoTracking().Where(g => g.UserId == userId);
        if (activeOnly) query = query.Where(g => g.GttStatus == "active");
        var rows = await query.OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id).ToListAsync(ct);
        var ids = rows.Select(r => r.GttId).ToList();
        var legs = await db.Set<SandboxGttLeg>().AsNoTracking().Where(l => ids.Contains(l.GttId)).OrderBy(l => l.LegNumber).ToListAsync(ct);
        static string Iso(DateTime? t) => t is null ? "" : Ist.FromUtc(DateTime.SpecifyKind(t.Value, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return new JsonArray(rows.Select(g =>
        {
            var own = legs.Where(l => l.GttId == g.GttId).ToList();
            return (JsonNode)new JsonObject
            {
                ["trigger_id"] = g.GttId, ["trigger_type"] = g.TriggerType, ["status"] = g.GttStatus, ["symbol"] = g.Symbol, ["exchange"] = g.Exchange,
                ["trigger_prices"] = new JsonArray(own.Select(l => (JsonNode)(double)l.TriggerPrice).ToArray()), ["last_price"] = (double)g.LastPrice,
                ["legs"] = new JsonArray(own.Select(l => (JsonNode)new JsonObject { ["action"] = l.Action, ["quantity"] = l.Quantity, ["price"] = (double)l.Price, ["pricetype"] = l.Pricetype, ["product"] = l.Product, ["triggered_order_id"] = l.TriggeredOrderId }).ToArray()),
                ["created_at"] = Iso(g.CreatedAt), ["updated_at"] = Iso(g.UpdatedAt), ["expires_at"] = Iso(g.ExpiresAt), ["strategy"] = g.Strategy, ["margin_blocked"] = (double)g.MarginBlocked,
            };
        }).ToArray());
    }

    /// <summary>Fires every resting GTT leg on this instrument that the price has reached. Returns how many fired.</summary>
    public async Task<int> MatchGttAsync(string exchange, string symbol, decimal ltp, CancellationToken ct = default)
    {
        List<(string GttId, int LegId)> due;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var rows = await (from g in db.Set<SandboxGtt>().AsNoTracking()
                              join l in db.Set<SandboxGttLeg>().AsNoTracking() on g.GttId equals l.GttId
                              where g.GttStatus == "active" && g.Exchange == exchange && g.Symbol == symbol && l.LegStatus == "pending"
                              select new { g.GttId, l.Id, l.TriggerDirection, l.TriggerPrice }).ToListAsync(ct);
            due = rows.Where(r => IsTriggeredBy(r.TriggerDirection, r.TriggerPrice, ltp)).Select(r => (r.GttId, r.Id)).ToList();
        }

        var fired = 0;
        foreach (var (gttId, legId) in due)
            if (await FireGttLegAsync(gttId, legId, ct)) fired++;
        return fired;
    }

    private async Task<bool> FireGttLegAsync(string gttId, int legId, CancellationToken ct)
    {
        // Step one claims the GTT (only one firing can win) and gives its margin back, so the order it places can reserve its own.
        var claimed = await WithRetryAsync<(string UserId, decimal Released, SandboxGttLeg Leg, string Symbol, string Exchange, string? Strategy)?>(async db =>
        {
            var won = await db.Set<SandboxGtt>().Where(g => g.GttId == gttId && g.GttStatus == "active")
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.GttStatus, "triggered").SetProperty(g => g.UpdatedAt, UtcNow), ct);
            if (won != 1) return null;
            var gtt = await db.Set<SandboxGtt>().SingleAsync(g => g.GttId == gttId, ct);
            var leg = await db.Set<SandboxGttLeg>().AsNoTracking().SingleAsync(l => l.Id == legId, ct);
            var released = gtt.MarginBlocked;
            gtt.MarginBlocked = 0;
            await ReleaseGttMarginAsync(db, gtt.UserId, released, ct);
            await db.Set<SandboxGttLeg>().Where(l => l.Id == legId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LegStatus, "triggering").SetProperty(l => l.ClaimedAt, UtcNow), ct);
            await db.SaveChangesAsync(ct);
            return (gtt.UserId, released, leg, gtt.Symbol, gtt.Exchange, gtt.Strategy);
        }, ct);
        if (claimed is not { } c) return false;

        DispatchResult result;
        try
        {
            result = await PlaceAsync(new OrderIntent
            {
                AccountId = Guid.ParseExact(c.UserId, "N"), Source = OrderSource.Gtt, Symbol = c.Symbol, Exchange = c.Exchange, Action = c.Leg.Action, Quantity = c.Leg.Quantity, Product = c.Leg.Product,
                PriceType = c.Leg.Pricetype, Price = c.Leg.Price, Strategy = c.Strategy ?? "GTT", Actor = "gtt",
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "GTT {GttId} could not place its order", gttId);
            result = new DispatchResult(DispatchStatus.Failed, "The sandbox could not place the order.");
        }

        if (result.Status != DispatchStatus.Accepted)
        {
            logger.LogWarning("GTT {GttId} triggered but its order was refused: {Message}", gttId, result.Message);
            await CompensateFailedFireAsync(gttId, legId, c.UserId, c.Released, ct);
            return false;
        }

        await WithRetryAsync(async db =>
        {
            await db.Set<SandboxGttLeg>().Where(l => l.Id == legId).ExecuteUpdateAsync(s => s.SetProperty(l => l.TriggeredOrderId, result.OrderId).SetProperty(l => l.LegStatus, "triggered").SetProperty(l => l.ClaimedAt, (DateTime?)null).SetProperty(l => l.UpdatedAt, UtcNow), ct);
            await db.Set<SandboxGttLeg>().Where(l => l.GttId == gttId && l.Id != legId && (l.LegStatus == "pending" || l.LegStatus == "triggering"))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.LegStatus, "cancelled").SetProperty(l => l.ClaimedAt, (DateTime?)null).SetProperty(l => l.UpdatedAt, UtcNow), ct);
            return true;
        }, ct);
        return true;
    }

    /// <summary>A trigger whose order was refused goes back to resting with its margin reserved again; if the funds can no longer carry it, it is cancelled rather than left holding nothing.</summary>
    private Task<bool> CompensateFailedFireAsync(string gttId, int legId, string userId, decimal released, CancellationToken ct) =>
        WithRetryAsync(async db =>
        {
            var funds = await LoadFundsAsync(db, userId, ct);
            var gtt = await db.Set<SandboxGtt>().SingleAsync(g => g.GttId == gttId, ct);
            if (released <= (funds.AvailableBalance ?? 0))
            {
                funds.AvailableBalance -= released;
                funds.UsedMargin += released;
                funds.UpdatedAt = UtcNow;
                gtt.MarginBlocked = released;
                gtt.GttStatus = "active";
                gtt.UpdatedAt = UtcNow;
                await db.Set<SandboxGttLeg>().Where(l => l.Id == legId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LegStatus, "pending").SetProperty(l => l.ClaimedAt, (DateTime?)null), ct);
            }
            else
            {
                gtt.GttStatus = "cancelled";
                gtt.UpdatedAt = UtcNow;
                await db.Set<SandboxGttLeg>().Where(l => l.GttId == gttId && (l.LegStatus == "pending" || l.LegStatus == "triggering")).ExecuteUpdateAsync(s => s.SetProperty(l => l.LegStatus, "cancelled").SetProperty(l => l.ClaimedAt, (DateTime?)null), ct);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    /// <summary>Marks resting GTTs past their expiry as expired and gives their margin back. Returns how many.</summary>
    public async Task<int> ExpireDueGttsAsync(CancellationToken ct = default)
    {
        List<string> due;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var now = UtcNow;
            due = await db.Set<SandboxGtt>().AsNoTracking().Where(g => g.GttStatus == "active" && g.ExpiresAt != null && g.ExpiresAt < now).Select(g => g.GttId).ToListAsync(ct);
        }

        var expired = 0;
        foreach (var id in due)
        {
            var done = await WithRetryAsync(async db =>
            {
                var gtt = await db.Set<SandboxGtt>().AsNoTracking().SingleOrDefaultAsync(g => g.GttId == id && g.GttStatus == "active", ct);
                if (gtt is null) return false;
                var won = await db.Set<SandboxGtt>().Where(g => g.GttId == id && g.GttStatus == "active").ExecuteUpdateAsync(s => s.SetProperty(g => g.GttStatus, "expired").SetProperty(g => g.MarginBlocked, 0m).SetProperty(g => g.UpdatedAt, UtcNow), ct);
                if (won != 1) return false;
                await db.Set<SandboxGttLeg>().Where(l => l.GttId == id && l.LegStatus == "pending").ExecuteUpdateAsync(s => s.SetProperty(l => l.LegStatus, "cancelled").SetProperty(l => l.ClaimedAt, (DateTime?)null), ct);
                await ReleaseGttMarginAsync(db, gtt.UserId, gtt.MarginBlocked, ct);
                await db.SaveChangesAsync(ct);
                return true;
            }, ct);
            if (done) expired++;
        }

        return expired;
    }
}
