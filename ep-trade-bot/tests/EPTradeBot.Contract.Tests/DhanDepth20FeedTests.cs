using EPTradeBot.Core.Streaming.Feeds;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>Dhan's 20-level depth socket against the Python adapter (tools/contract-fixtures/feed_dhan20.fixtures.json).</summary>
public sealed class DhanDepth20FeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_dhan20.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_dhan20.fixtures.json", name, () => new DhanDepth20FeedCodec(), dropRepeatedRequests: true);
}
