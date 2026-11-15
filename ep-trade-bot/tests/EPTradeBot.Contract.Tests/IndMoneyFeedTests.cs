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
}
