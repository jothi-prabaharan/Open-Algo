using EPTradeBot.Core.Streaming.Feeds;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>IndMoney's stream, replayed against what OpenAlgo's adapter and socket client sent and published (tools/contract-fixtures/feed_indmoney.fixtures.json).</summary>
public sealed class IndMoneyFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_indmoney.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_indmoney.fixtures.json", name, () => new IndMoneyFeedCodec());

    [Fact]
    public void An_unsubscribe_with_no_mode_ends_each_mode_the_symbol_holds()
    {
        var codec = new IndMoneyFeedCodec();
        var sbin = new FeedTarget("NSE", "SBIN", "3045", "NSE");
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", EPTradeBot.Core.Streaming.StreamMode.Ltp), new FeedRequest("NSE", "SBIN", "3045", "NSE", EPTradeBot.Core.Streaming.StreamMode.Quote), new FeedRequest("NSE", "INFY", "1594", "NSE", EPTradeBot.Core.Streaming.StreamMode.Quote)], 0);
        var frames = codec.Unsubscribe(sbin, null, 0);
        Assert.Equal(["""{"action":"unsubscribe","mode":"ltp","instruments":["NSE:3045"]}""", """{"action":"unsubscribe","mode":"quote","instruments":["NSE:3045"]}"""], frames);
        Assert.Empty(codec.Unsubscribe(sbin, null, 0));
    }
}
