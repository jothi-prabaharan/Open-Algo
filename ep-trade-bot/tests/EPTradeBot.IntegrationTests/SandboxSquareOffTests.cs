using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Core.Time;
using Microsoft.EntityFrameworkCore;
using EPTradeBot.Core.Sandbox;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Sandbox;

namespace EPTradeBot.IntegrationTests;

/// <summary>The sandbox's end-of-day clean-up: intraday orders are cancelled and intraday positions closed from each exchange's cut-off, and nothing carried overnight is touched.</summary>
public sealed class SandboxSquareOffTests
{
    private static OrderIntent Intent(Guid account, string symbol, string exchange, string action, int quantity, string product, string priceType = "MARKET", decimal price = 0) => new()
    {
        AccountId = account, Source = OrderSource.Manual, Symbol = symbol, Exchange = exchange, Action = action, Quantity = quantity, Product = product, PriceType = priceType, Price = price,
    };

    [Theory]
    [InlineData("15:15", 15, 15)]
    [InlineData("09:05", 9, 5)]
    [InlineData("24:00", 15, 15)]
    [InlineData("nonsense", 15, 15)]
    [InlineData("", 15, 15)]
    [InlineData("15:75", 15, 15)]
    public void The_cut_off_time_is_read_or_falls_back_to_the_equity_default(string text, int hour, int minute) => Assert.Equal(new TimeOnly(hour, minute), SandboxSquareOff.Parse(text));

    [Theory]
    [InlineData("NSE", "15:15")]
    [InlineData("NFO", "15:15")]
    [InlineData("BFO", "15:15")]
    [InlineData("CDS", "16:45")]
    [InlineData("BCD", "16:45")]
    [InlineData("MCX", "23:30")]
    [InlineData("NCDEX", "17:00")]
    public void Each_exchange_has_its_own_cut_off(string exchange, string expected) =>
        Assert.Equal(TimeOnly.Parse(expected), SandboxSquareOff.TimeFor(exchange, "15:15", "16:45", "23:30", "17:00"));

    [Fact]
    public void An_exchange_with_no_cut_off_is_never_due_and_the_cut_off_minute_itself_is_due()
    {
        Assert.Null(SandboxSquareOff.TimeFor("NSE_INDEX", "15:15", "16:45", "23:30", "17:00"));
        Assert.False(SandboxSquareOff.IsDue(null, new TimeOnly(23, 59)));
        Assert.False(SandboxSquareOff.IsDue(new TimeOnly(15, 15), new TimeOnly(15, 14)));
        Assert.True(SandboxSquareOff.IsDue(new TimeOnly(15, 15), new TimeOnly(15, 15)));
        Assert.True(SandboxSquareOff.IsDue(new TimeOnly(15, 15), new TimeOnly(22, 0)));
    }

    [PostgresFact]
    public async Task From_the_cut_off_intraday_orders_are_cancelled_and_intraday_positions_closed_but_overnight_ones_stay()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-sq", 10_000, 1000, 10_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        host.SetQuote("NSE", "INFY", 1500);
        host.SetQuote("MCX", "GOLDM", 70000);
        var sandbox = host.Get<SandboxEngine>();

        await sandbox.PlaceAsync(Intent(id, "SBIN", "NSE", "BUY", 10, "MIS"));
        await sandbox.PlaceAsync(Intent(id, "INFY", "NSE", "SELL", 4, "MIS"));
        await sandbox.PlaceAsync(Intent(id, "SBIN", "NSE", "BUY", 5, "CNC"));
        await sandbox.PlaceAsync(Intent(id, "GOLDM", "MCX", "BUY", 1, "MIS"));
        var resting = await sandbox.PlaceAsync(Intent(id, "SBIN", "NSE", "BUY", 3, "MIS", "LIMIT", 450));
        Assert.Equal(DispatchStatus.Accepted, resting.Status);
        var before = await sandbox.GetFundsAsync(id);

        Assert.Equal((0, 0), await sandbox.SquareOffAsync(new TimeOnly(15, 14)));

        var (cancelled, closed) = await sandbox.SquareOffAsync(new TimeOnly(15, 15));
        Assert.Equal(1, cancelled);
        Assert.Equal(2, closed);

        var positions = await sandbox.GetPositionsAsync(id);
        Assert.Equal(0, positions.Single(p => p.Symbol == "SBIN" && p.Product == "MIS").Quantity);
        Assert.Equal(0, positions.Single(p => p.Symbol == "INFY").Quantity);
        Assert.Equal(5, positions.Single(p => p.Symbol == "SBIN" && p.Product == "CNC").Quantity);
        Assert.Equal(1, positions.Single(p => p.Symbol == "GOLDM").Quantity);
        Assert.Contains(await sandbox.GetOrdersAsync(id), o => o.Orderid == resting.OrderId && o.OrderStatus == "cancelled");
        Assert.True((await sandbox.GetFundsAsync(id)).AvailableBalance > before.AvailableBalance);

        // The commodity position goes at its own, later, cut-off; a second sweep finds nothing left to do for the equity ones.
        Assert.Equal((0, 0), await sandbox.SquareOffAsync(new TimeOnly(15, 30)));
        Assert.Equal((0, 1), await sandbox.SquareOffAsync(new TimeOnly(23, 30)));
        Assert.Equal(0, (await sandbox.GetPositionsAsync(id)).Single(p => p.Symbol == "GOLDM").Quantity);
        Assert.Equal((0, 0), await sandbox.SquareOffAsync(new TimeOnly(23, 31)));
    }

    [PostgresFact]
    public async Task After_a_restart_an_intraday_position_from_an_earlier_session_is_settled_into_the_all_time_total_only()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-catch", 10_000, 1000, 10_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        host.SetQuote("NSE", "INFY", 1500);
        host.SetQuote("NSE", "TCS", 3500);
        var sandbox = host.Get<SandboxEngine>();
        await sandbox.PlaceAsync(Intent(id, "SBIN", "NSE", "BUY", 10, "MIS"));
        await sandbox.PlaceAsync(Intent(id, "INFY", "NSE", "SELL", 2, "MIS"));
        await sandbox.PlaceAsync(Intent(id, "TCS", "NSE", "BUY", 1, "MIS"));
        var userId = SandboxEngine.UserId(id);
        var boundary = Ist.LastSessionExpiryUtc(DateTime.UtcNow);
        decimal margin, shortMargin;
        await using (var db = host.NewDb())
        {
            // SBIN was left open by an earlier session, last priced at 520; INFY was traded since the boundary.
            var stale = await db.Set<SandboxPosition>().SingleAsync(p => p.UserId == userId && p.Symbol == "SBIN");
            margin = stale.MarginBlocked ?? 0;
            stale.UpdatedAt = boundary.AddDays(-1);
            stale.Ltp = 520;
            // INFY was sold short at 1500 and last priced at 1480: a gain of 40.
            var shortStale = await db.Set<SandboxPosition>().SingleAsync(p => p.UserId == userId && p.Symbol == "INFY");
            shortMargin = shortStale.MarginBlocked ?? 0;
            shortStale.UpdatedAt = boundary.AddDays(-1);
            shortStale.Ltp = 1480;
            await db.SaveChangesAsync();
        }

        var before = await sandbox.GetFundsAsync(id);
        Assert.Equal(2, await sandbox.CatchUpStaleMisAsync());

        var positions = await sandbox.GetPositionsAsync(id);
        var settled = positions.Single(p => p.Symbol == "SBIN");
        Assert.Equal(0, settled.Quantity);
        Assert.Equal(0m, settled.MarginBlocked);
        Assert.Equal(200m, settled.AccumulatedRealizedPnl - 0m);
        Assert.Equal(0m, settled.TodayRealizedPnl);
        Assert.Equal(0, positions.Single(p => p.Symbol == "INFY").Quantity);
        Assert.Equal(40m, positions.Single(p => p.Symbol == "INFY").AccumulatedRealizedPnl);
        Assert.Equal(1, positions.Single(p => p.Symbol == "TCS").Quantity);

        var after = await sandbox.GetFundsAsync(id);
        Assert.Equal(before.AvailableBalance + margin + shortMargin + 240m, after.AvailableBalance);
        Assert.Equal(before.UsedMargin - margin - shortMargin, after.UsedMargin);
        Assert.Equal(before.RealizedPnl + 240m, after.RealizedPnl);
        Assert.Equal(before.TodayRealizedPnl, after.TodayRealizedPnl);

        // Run again after the restart: nothing is left to settle.
        Assert.Equal(0, await sandbox.CatchUpStaleMisAsync());
    }
}
