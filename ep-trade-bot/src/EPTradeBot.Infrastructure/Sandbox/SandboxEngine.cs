using System.Security.Cryptography;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Core.Sandbox;
using EPTradeBot.Core.Time;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EPTradeBot.Infrastructure.Sandbox;

/// <summary>
/// The API Analyzer (sandbox) engine for PAPER accounts, on the <c>sandbox</c> schema.
///
/// Atomicity: an order row, its margin deduction and, when it fills at once, its trade
/// and position change are written in one transaction. The funds, position and order
/// rows carry a row version (<c>xmin</c>), so two writers that read the same balance
/// cannot both commit; the loser re-reads and decides again. Nothing here can spend
/// margin twice or leave an order without its margin.
///
/// Scope of this port: market, limit, SL and SL-M orders, leverage-based margin,
/// positions with average price and realised P&amp;L, cancel, square-off. Not yet ported:
/// GTT. Intraday (MIS) positions and orders are closed by the square-off sweep, delivery (CNC) positions become holdings the next day, and a restart settles what it missed.
/// </summary>
public sealed class SandboxEngine(
    IDbContextFactory<EpTradeDbContext> contexts,
    IQuoteSource quotes,
    IOptions<SandboxOptions> options,
    TimeProvider clock,
    ILogger<SandboxEngine> logger)
{
    private const int MaxAttempts = 8;
    private readonly SandboxLeverage _leverage = new();

    public static string UserId(Guid accountId) => accountId.ToString("N");

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>Places an order for a PAPER account. Fills immediately when the quote allows.</summary>
    public Task<DispatchResult> PlaceAsync(OrderIntent intent, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async db =>
        {
            var userId = UserId(intent.AccountId);
            var funds = await LoadFundsAsync(db, userId, cancellationToken);
            var position = await db.Set<SandboxPosition>().SingleOrDefaultAsync(
                p => p.UserId == userId && p.Symbol == intent.Symbol && p.Exchange == intent.Exchange && p.Product == intent.Product, cancellationToken);
            var net = position?.Quantity ?? 0;
            var quote = quotes.Get(intent.Exchange, intent.Symbol);
            var opening = SandboxRules.OpeningQuantity(net, intent.Sign, intent.Quantity);

            decimal margin = 0;
            if (opening > 0)
            {
                var marginPrice = intent.PriceType switch
                {
                    "LIMIT" or "SL" => intent.Price,
                    "SL-M" => intent.TriggerPrice,
                    _ => intent.IsBuy ? (quote?.Ask is > 0 ? quote.Ask : quote?.Ltp) : (quote?.Bid is > 0 ? quote.Bid : quote?.Ltp),
                };
                if (marginPrice is not { } mp || mp <= 0)
                    return new DispatchResult(DispatchStatus.Refused,
                        $"There is no price for {intent.Symbol} yet, so the sandbox cannot work out the margin. Try again once the price is showing.", Code: "price_unavailable");
                margin = SandboxRules.Margin(_leverage, intent.Exchange, intent.Product, intent.Symbol, intent.IsBuy, opening, mp);
                if (margin > (funds.AvailableBalance ?? 0))
                    return new DispatchResult(DispatchStatus.Refused,
                        $"Not enough sandbox funds: this order needs INR {margin:N2} of margin and INR {funds.AvailableBalance ?? 0:N2} is available.", Code: "insufficient_funds");
            }

            var now = UtcNow;
            var order = new SandboxOrder
            {
                Orderid = NewId("SB"),
                UserId = userId,
                Strategy = intent.Strategy,
                Symbol = intent.Symbol,
                Exchange = intent.Exchange,
                Action = intent.IsBuy ? "BUY" : "SELL",
                Quantity = intent.Quantity,
                Price = intent.Price,
                TriggerPrice = intent.TriggerPrice,
                PriceType = intent.PriceType,
                Product = intent.Product,
                OrderStatus = "open",
                FilledQuantity = 0,
                PendingQuantity = intent.Quantity,
                MarginBlocked = margin,
                OrderTimestamp = now,
                UpdateTimestamp = now,
            };
            db.Add(order);
            funds.AvailableBalance -= margin;
            funds.UsedMargin += margin;
            funds.UpdatedAt = now;

            var fillPrice = SandboxRules.FillPrice(intent.PriceType, intent.IsBuy, intent.Price, intent.TriggerPrice, quote?.Ltp, quote?.Bid, quote?.Ask);
            string message;
            if (fillPrice is { } fp)
            {
                var filled = Fill(db, order, funds, position, fp, quote?.Ltp);
                message = filled ? $"Filled {intent.Quantity} {intent.Symbol} at {fp:0.00} in the sandbox." : order.RejectionReason ?? "Rejected.";
            }
            else
            {
                message = $"Order {order.Orderid} is open in the sandbox and fills when the price reaches it.";
            }

            await db.SaveChangesAsync(cancellationToken);
            return order.OrderStatus == "rejected"
                ? new DispatchResult(DispatchStatus.Refused, message, order.Orderid, "rejected_at_fill")
                : new DispatchResult(DispatchStatus.Accepted, message, order.Orderid);
        }, cancellationToken);

    /// <summary>Cancels one open order and returns its blocked margin. False when it was not open.</summary>
    public Task<bool> CancelAsync(Guid accountId, string orderId, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async db =>
        {
            var userId = UserId(accountId);
            var order = await db.Set<SandboxOrder>().SingleOrDefaultAsync(o => o.UserId == userId && o.Orderid == orderId, cancellationToken);
            if (order is null || order.OrderStatus != "open") return false;
            var funds = await LoadFundsAsync(db, userId, cancellationToken);
            Cancel(order, funds);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>Cancels every open order of the account in one transaction. Returns the ids cancelled.</summary>
    public Task<IReadOnlyList<string>> CancelAllAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        WithRetryAsync<IReadOnlyList<string>>(async db =>
        {
            var userId = UserId(accountId);
            var open = await db.Set<SandboxOrder>().Where(o => o.UserId == userId && o.OrderStatus == "open").ToListAsync(cancellationToken);
            if (open.Count == 0) return [];
            var funds = await LoadFundsAsync(db, userId, cancellationToken);
            foreach (var o in open) Cancel(o, funds);
            await db.SaveChangesAsync(cancellationToken);
            return open.Select(o => o.Orderid).ToList();
        }, cancellationToken);

    /// <summary>
    /// Re-checks the open orders for one instrument against its latest quote, filling
    /// those the price has reached. Each fill commits on its own, so one refused fill
    /// does not hold back the others.
    /// </summary>
    public async Task<int> MatchAsync(string exchange, string symbol, CancellationToken cancellationToken = default)
    {
        var quote = quotes.Get(exchange, symbol);
        if (quote?.Ltp is not > 0) return 0;
        List<(string UserId, string OrderId)> candidates;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            candidates = (await db.Set<SandboxOrder>().AsNoTracking()
                    .Where(o => o.Exchange == exchange && o.Symbol == symbol && o.OrderStatus == "open")
                    .Select(o => new { o.UserId, o.Orderid })
                    .ToListAsync(cancellationToken))
                .Select(o => (o.UserId, o.Orderid)).ToList();
        }

        var filled = 0;
        foreach (var (userId, orderId) in candidates)
        {
            var done = await WithRetryAsync(async db =>
            {
                var order = await db.Set<SandboxOrder>().SingleAsync(o => o.Orderid == orderId, cancellationToken);
                if (order.OrderStatus != "open") return false;
                var price = SandboxRules.FillPrice(order.PriceType, order.Action == "BUY", order.Price ?? 0, order.TriggerPrice ?? 0, quote.Ltp, quote.Bid, quote.Ask);
                if (price is not { } fp) return false;
                var funds = await LoadFundsAsync(db, userId, cancellationToken);
                var position = await db.Set<SandboxPosition>().SingleOrDefaultAsync(
                    p => p.UserId == userId && p.Symbol == order.Symbol && p.Exchange == order.Exchange && p.Product == order.Product, cancellationToken);
                var ok = Fill(db, order, funds, position, fp, quote.Ltp);
                await db.SaveChangesAsync(cancellationToken);
                return ok;
            }, cancellationToken);
            if (done) filled++;
        }

        return filled;
    }

    /// <summary>
    /// The auto square-off: from each exchange's cut-off time, its open MIS orders are cancelled (returning their margin) and its MIS positions closed at the market.
    /// Safe to run repeatedly: what is already closed or cancelled is not found again.
    /// </summary>
    public async Task<(int Cancelled, int Closed)> SquareOffAsync(TimeOnly nowIst, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        TimeOnly? Due(string exchange) => SandboxSquareOff.TimeFor(exchange, o.NseBseSquareOffTime, o.CdsBcdSquareOffTime, o.McxSquareOffTime, o.NcdexSquareOffTime);

        List<(string UserId, string OrderId, string Exchange)> orders;
        List<(string UserId, string Symbol, string Exchange, int Quantity)> positions;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            orders = (await db.Set<SandboxOrder>().AsNoTracking().Where(x => x.Product == "MIS" && x.OrderStatus == "open").Select(x => new { x.UserId, x.Orderid, x.Exchange }).ToListAsync(cancellationToken))
                .Where(x => SandboxSquareOff.IsDue(Due(x.Exchange), nowIst)).Select(x => (x.UserId, x.Orderid, x.Exchange)).ToList();
            positions = (await db.Set<SandboxPosition>().AsNoTracking().Where(x => x.Product == "MIS" && x.Quantity != 0).Select(x => new { x.UserId, x.Symbol, x.Exchange, x.Quantity }).ToListAsync(cancellationToken))
                .Where(x => SandboxSquareOff.IsDue(Due(x.Exchange), nowIst)).Select(x => (x.UserId, x.Symbol, x.Exchange, x.Quantity)).ToList();
        }

        var cancelled = 0;
        foreach (var (userId, orderId, _) in orders)
        {
            if (await CancelAsync(Guid.ParseExact(userId, "N"), orderId, cancellationToken)) cancelled++;
        }

        var closed = 0;
        foreach (var (userId, symbol, exchange, quantity) in positions)
        {
            try
            {
                var result = await PlaceAsync(new OrderIntent
                {
                    AccountId = Guid.ParseExact(userId, "N"), Source = EPTradeBot.Core.Entities.Audit.OrderSource.SquareOff, Symbol = symbol, Exchange = exchange,
                    Action = quantity > 0 ? "SELL" : "BUY", Quantity = Math.Abs(quantity), Product = "MIS", PriceType = "MARKET", Actor = "square-off",
                }, cancellationToken);
                if (result.Status == DispatchStatus.Accepted) closed++;
                else logger.LogWarning("Auto square-off of {Symbol} was refused: {Message}", symbol, result.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Auto square-off of {Symbol} failed", symbol);
            }
        }

        return (cancelled, closed);
    }

    /// <summary>
    /// Settles intraday positions that outlived their session (the platform was down at the cut-off): each is closed at its last known price, its margin released, and its profit or loss
    /// goes to the all-time total only, because the day it belongs to is over. A position traded since the boundary is left alone. Returns how many were settled.
    /// </summary>
    public async Task<int> CatchUpStaleMisAsync(CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var boundary = Ist.LastSessionExpiryUtc(UtcNow);
        List<int> ids;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            ids = (await db.Set<SandboxPosition>().AsNoTracking().Where(p => p.Product == "MIS" && p.Quantity != 0 && p.UpdatedAt < boundary).Select(p => new { p.Id, p.Exchange }).ToListAsync(cancellationToken))
                .Where(p => SandboxSquareOff.TimeFor(p.Exchange, o.NseBseSquareOffTime, o.CdsBcdSquareOffTime, o.McxSquareOffTime, o.NcdexSquareOffTime) is not null).Select(p => p.Id).ToList();
        }

        var settled = 0;
        foreach (var id in ids)
        {
            var done = await WithRetryAsync(async db =>
            {
                var position = await db.Set<SandboxPosition>().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
                // The test is made again under the write: a fill since the query makes it today's position.
                if (position is null || position.Quantity == 0 || position.UpdatedAt >= boundary) return false;
                var price = position.Ltp is > 0 ? position.Ltp.Value : position.AveragePrice;
                var quantity = position.Quantity;
                var pnl = quantity > 0 ? (price - position.AveragePrice) * quantity : (position.AveragePrice - price) * Math.Abs(quantity);
                var margin = position.MarginBlocked ?? 0;
                var funds = await db.Set<SandboxFund>().SingleOrDefaultAsync(f => f.UserId == position.UserId, cancellationToken);
                if (funds is not null)
                {
                    funds.AvailableBalance += margin + pnl;
                    funds.UsedMargin = Math.Max(0m, (funds.UsedMargin ?? 0m) - margin);
                    funds.RealizedPnl += pnl;
                    funds.TotalPnl = funds.RealizedPnl + (funds.UnrealizedPnl ?? 0);
                }

                position.Quantity = 0;
                position.MarginBlocked = 0;
                position.Pnl = pnl;
                position.AccumulatedRealizedPnl += pnl;
                position.TodayRealizedPnl = 0;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Catch-up settled the stale intraday position in {Symbol}: quantity {Quantity}, profit or loss {Pnl}.", position.Symbol, quantity, pnl);
                return true;
            }, cancellationToken);
            if (done) settled++;
        }

        return settled;
    }

    /// <summary>
    /// T+1 settlement: delivery (CNC) positions opened before today (Indian calendar) become holdings. A purchase adds to the holding at the weighted average and moves its margin out of
    /// the reserved total; a sale reduces the holding and credits the proceeds. A position another run already settled is not found again. Returns how many were settled.
    /// </summary>
    public async Task<int> SettleT1Async(DateTime nowIst, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(nowIst);
        var cutoffUtc = Ist.ToUtc(nowIst.Date);
        List<int> ids;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
            ids = await db.Set<SandboxPosition>().AsNoTracking().Where(p => p.Product == "CNC" && p.CreatedAt < cutoffUtc).Select(p => p.Id).ToListAsync(cancellationToken);

        var settled = 0;
        foreach (var id in ids)
        {
            var done = await WithRetryAsync(async db =>
            {
                var position = await db.Set<SandboxPosition>().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
                if (position is null || position.CreatedAt >= cutoffUtc) return false;
                if (position.Quantity == 0)
                {
                    db.Remove(position);
                    await db.SaveChangesAsync(cancellationToken);
                    return false;
                }

                var funds = await db.Set<SandboxFund>().SingleOrDefaultAsync(f => f.UserId == position.UserId, cancellationToken);
                var holding = await db.Set<SandboxHolding>().SingleOrDefaultAsync(h => h.UserId == position.UserId && h.Symbol == position.Symbol && h.Exchange == position.Exchange, cancellationToken);
                var value = Math.Abs(position.Quantity) * position.AveragePrice;
                var now = UtcNow;
                if (holding is not null)
                {
                    if (position.Quantity > 0)
                    {
                        var totalQuantity = Math.Abs(holding.Quantity) + Math.Abs(position.Quantity);
                        holding.AveragePrice = totalQuantity > 0 ? (Math.Abs(holding.Quantity) * holding.AveragePrice + value) / totalQuantity : holding.AveragePrice;
                        holding.Quantity += position.Quantity;
                        if (funds is not null) funds.UsedMargin = (funds.UsedMargin ?? 0m) - Math.Min(value, funds.UsedMargin ?? 0m);
                    }
                    else
                    {
                        holding.Quantity += position.Quantity;
                        if (funds is not null) funds.AvailableBalance += value;
                    }

                    holding.Ltp = position.Ltp;
                    holding.UpdatedAt = now;
                    if (holding.Quantity == 0) db.Remove(holding);
                }
                else
                {
                    db.Add(new SandboxHolding
                    {
                        UserId = position.UserId, Symbol = position.Symbol, Exchange = position.Exchange, Quantity = position.Quantity, AveragePrice = position.AveragePrice,
                        Ltp = position.Ltp ?? position.AveragePrice, Pnl = 0m, PnlPercent = 0m, SettlementDate = today, CreatedAt = now, UpdatedAt = now,
                    });
                    if (funds is not null) funds.UsedMargin = (funds.UsedMargin ?? 0m) - Math.Min(value, funds.UsedMargin ?? 0m);
                }

                if (funds is not null) funds.UpdatedAt = now;
                db.Remove(position);
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);
            if (done) settled++;
        }

        return settled;
    }

    /// <summary>The account's holdings, marked to the latest quotes (a holding with no quote keeps the figures it was last given).</summary>
    public async Task<IReadOnlyList<SandboxHolding>> GetHoldingsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var userId = UserId(accountId);
        return await WithRetryAsync<IReadOnlyList<SandboxHolding>>(async db =>
        {
            var holdings = await db.Set<SandboxHolding>().Where(h => h.UserId == userId && h.Quantity != 0).OrderBy(h => h.Symbol).ThenBy(h => h.Exchange).ToListAsync(cancellationToken);
            foreach (var h in holdings)
            {
                if (quotes.Get(h.Exchange, h.Symbol)?.Ltp is not { } ltp || ltp <= 0) continue;
                h.Ltp = ltp;
                h.Pnl = (ltp - h.AveragePrice) * Math.Abs(h.Quantity);
                h.PnlPercent = h.AveragePrice > 0 ? (ltp - h.AveragePrice) / h.AveragePrice * 100m : 0m;
            }

            await db.SaveChangesAsync(cancellationToken);
            return holdings;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var userId = UserId(accountId);
        return await db.Set<SandboxPosition>().AsNoTracking()
            .Where(p => p.UserId == userId && p.Quantity != 0)
            .Select(p => new OpenPosition(p.Symbol, p.Exchange, p.Product, p.Quantity))
            .ToListAsync(cancellationToken);
    }

    /// <summary>What the account risk rules need, from the sandbox book and the latest quotes.</summary>
    public async Task<AccountExposure> GetExposureAsync(OrderIntent intent, int freezeQuantity, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var userId = UserId(intent.AccountId);
        var positions = await db.Set<SandboxPosition>().AsNoTracking().Where(p => p.UserId == userId).ToListAsync(cancellationToken);
        var sessionStart = Ist.LastSessionExpiryUtc(UtcNow);

        decimal unrealized = 0, gross = 0, realizedToday = 0;
        var net = 0;
        foreach (var p in positions)
        {
            var state = State(p);
            realizedToday += SandboxDayPnl.TodayRealized(state, sessionStart);
            if (p.Symbol == intent.Symbol && p.Exchange == intent.Exchange && p.Product == intent.Product) net = p.Quantity;
            if (p.Quantity == 0) continue;
            // Every open position counts towards the limits, whichever session opened it.
            var mark = Mark(p);
            unrealized += SandboxDayPnl.Unrealized(state, mark);
            gross += Math.Abs(p.Quantity) * mark;
        }

        return new AccountExposure
        {
            DayPnl = realizedToday + unrealized,
            GrossOpenValue = gross,
            NetQuantityInInstrument = net,
            MarkPrice = quotes.Get(intent.Exchange, intent.Symbol)?.Ltp,
            FreezeQuantity = freezeQuantity,
        };
    }

    /// <summary>
    /// The day's P&amp;L by position, as OpenAlgo's sandbox position book lists it: positions of this
    /// session, and NRML ones carried from an earlier session, in the order they were opened.
    /// Open positions are marked to the latest quote, else to the price stored with them.
    /// </summary>
    public async Task<IReadOnlyList<SandboxDayPnlRow>> GetDayPnlAsync(Guid accountId, string broker, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var userId = UserId(accountId);
        var positions = await db.Set<SandboxPosition>().AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.Id).ToListAsync(cancellationToken);
        var sessionStart = Ist.LastSessionExpiryUtc(UtcNow);
        var listed = positions.Where(p => SandboxDayPnl.Listed(State(p), sessionStart)).ToList();

        var symbols = listed.Select(p => p.Symbol).Distinct().ToList();
        var contractValues = (await db.Set<SymToken>().AsNoTracking()
                .Where(t => t.Broker == broker && symbols.Contains(t.Symbol) && t.ContractValue != null)
                .Select(t => new { t.Exchange, t.Symbol, t.ContractValue })
                .ToListAsync(cancellationToken))
            .GroupBy(t => (t.Exchange, t.Symbol))
            .ToDictionary(g => g.Key, g => (decimal)g.First().ContractValue!.Value);

        return listed.Select(p => SandboxDayPnl.Row(State(p), Mark(p),
            contractValues.TryGetValue((p.Exchange, p.Symbol), out var cv) && cv > 0 ? cv : 1m, sessionStart)).ToList();
    }

    private static SandboxPositionState State(SandboxPosition p) =>
        new(p.Symbol, p.Exchange, p.Product, p.Quantity, p.AveragePrice, p.TodayRealizedPnl ?? 0, DateTime.SpecifyKind(p.UpdatedAt, DateTimeKind.Utc));

    private decimal Mark(SandboxPosition p) => quotes.Get(p.Exchange, p.Symbol)?.Ltp ?? p.Ltp ?? p.AveragePrice;

    public async Task<SandboxFund> GetFundsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var funds = await LoadFundsAsync(db, UserId(accountId), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return funds;
    }

    public async Task<IReadOnlyList<SandboxOrder>> GetOrdersAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var userId = UserId(accountId);
        return await db.Set<SandboxOrder>().AsNoTracking().Where(o => o.UserId == userId).OrderByDescending(o => o.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SandboxTrade>> GetTradesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var userId = UserId(accountId);
        return await db.Set<SandboxTrade>().AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SandboxPosition>> GetPositionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var userId = UserId(accountId);
        return await db.Set<SandboxPosition>().AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.Symbol).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Applies a fill inside the caller's transaction. The order's blocked margin is
    /// released and the margin for the opening part, at the fill price, is charged; a
    /// fill the funds cannot carry is rejected rather than taking the balance negative.
    /// </summary>
    private bool Fill(EpTradeDbContext db, SandboxOrder order, SandboxFund funds, SandboxPosition? position, decimal price, decimal? ltp)
    {
        var isBuy = order.Action == "BUY";
        var sign = isBuy ? 1 : -1;
        var now = UtcNow;
        var blocked = order.MarginBlocked ?? 0;
        funds.AvailableBalance += blocked;
        funds.UsedMargin -= blocked;
        order.MarginBlocked = 0;

        var net = position?.Quantity ?? 0;
        var opening = SandboxRules.OpeningQuantity(net, sign, order.Quantity);
        var openingMargin = opening == 0 ? 0 : SandboxRules.Margin(_leverage, order.Exchange, order.Product, order.Symbol, isBuy, opening, price);
        if (openingMargin > (funds.AvailableBalance ?? 0))
        {
            order.OrderStatus = "rejected";
            order.RejectionReason = $"Not enough sandbox funds when the order filled: it needed INR {openingMargin:N2} and INR {funds.AvailableBalance ?? 0:N2} was available.";
            order.UpdateTimestamp = now;
            return false;
        }

        if (position is null)
        {
            position = new SandboxPosition
            {
                UserId = order.UserId, Symbol = order.Symbol, Exchange = order.Exchange, Product = order.Product,
                Quantity = 0, AveragePrice = 0, MarginBlocked = 0, AccumulatedRealizedPnl = 0, TodayRealizedPnl = 0, CreatedAt = now, UpdatedAt = now,
            };
            db.Add(position);
        }

        // Today's realized P&L starts again at the session boundary (03:00 IST), the boundary every reader uses.
        var sessionStart = Ist.LastSessionExpiryUtc(now);
        if (position.UpdatedAt < sessionStart) position.TodayRealizedPnl = 0;
        if (funds.UpdatedAt < sessionStart) funds.TodayRealizedPnl = 0;

        var outcome = SandboxRules.ApplyFill(position.Quantity, position.AveragePrice, position.MarginBlocked ?? 0, sign, order.Quantity, price, openingMargin);
        funds.AvailableBalance = funds.AvailableBalance - openingMargin + outcome.MarginReleased + outcome.RealizedPnl;
        funds.UsedMargin = funds.UsedMargin + openingMargin - outcome.MarginReleased;
        funds.RealizedPnl += outcome.RealizedPnl;
        funds.TodayRealizedPnl += outcome.RealizedPnl;
        funds.TotalPnl = funds.RealizedPnl + (funds.UnrealizedPnl ?? 0);
        funds.UpdatedAt = now;

        position.Quantity = outcome.NetQuantity;
        position.AveragePrice = outcome.AveragePrice;
        position.MarginBlocked = outcome.MarginRemaining;
        position.AccumulatedRealizedPnl += outcome.RealizedPnl;
        position.TodayRealizedPnl += outcome.RealizedPnl;
        position.Ltp = ltp ?? price;
        position.Pnl = (position.Ltp - position.AveragePrice) * position.Quantity;
        position.UpdatedAt = now;

        order.OrderStatus = "complete";
        order.AveragePrice = price;
        order.FilledQuantity = order.Quantity;
        order.PendingQuantity = 0;
        order.UpdateTimestamp = now;

        db.Add(new SandboxTrade
        {
            Tradeid = NewId("ST"), Orderid = order.Orderid, UserId = order.UserId, Symbol = order.Symbol, Exchange = order.Exchange,
            Action = order.Action, Quantity = order.Quantity, Price = price, Product = order.Product, Strategy = order.Strategy, TradeTimestamp = now,
        });
        return true;
    }

    private void Cancel(SandboxOrder order, SandboxFund funds)
    {
        var blocked = order.MarginBlocked ?? 0;
        funds.AvailableBalance += blocked;
        funds.UsedMargin -= blocked;
        funds.UpdatedAt = UtcNow;
        order.MarginBlocked = 0;
        order.OrderStatus = "cancelled";
        order.UpdateTimestamp = UtcNow;
    }

    private async Task<SandboxFund> LoadFundsAsync(EpTradeDbContext db, string userId, CancellationToken cancellationToken)
    {
        var funds = await db.Set<SandboxFund>().SingleOrDefaultAsync(f => f.UserId == userId, cancellationToken);
        if (funds is not null) return funds;
        var capital = options.Value.StartingCapital;
        funds = new SandboxFund
        {
            UserId = userId, TotalCapital = capital, AvailableBalance = capital, UsedMargin = 0, RealizedPnl = 0,
            TodayRealizedPnl = 0, UnrealizedPnl = 0, TotalPnl = 0, ResetCount = 0,
        };
        db.Add(funds);
        return funds;
    }

    /// <summary>
    /// Runs one unit of work in a fresh context and transaction, retrying from a clean
    /// read when a concurrent writer committed first (row version conflict or a unique
    /// insert race). Any other failure propagates.
    /// </summary>
    private async Task<T> WithRetryAsync<T>(Func<EpTradeDbContext, Task<T>> work, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await work(db);
                await tx.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsConflict(ex))
            {
                await tx.RollbackAsync(CancellationToken.None);
                logger.LogDebug("Sandbox write conflicted with a concurrent writer; retrying (attempt {Attempt}).", attempt);
                await Task.Delay(Random.Shared.Next(2, 10 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsConflict(Exception ex) =>
        ex is DbUpdateConcurrencyException
        || ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } };

    private static string NewId(string prefix) =>
        $"{prefix}{DateTime.UtcNow:yyMMddHHmmssfff}{RandomNumberGenerator.GetInt32(100000, 999999)}";
}
