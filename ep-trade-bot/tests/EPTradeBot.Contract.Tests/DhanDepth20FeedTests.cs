using EPTradeBot.Core.Streaming;
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

/// <summary>Dhan's 20-level codec called directly: the limits and requests the recorded cases do not reach.</summary>
public sealed class DhanDepth20FeedUnitTests
{
    private static FeedRequest Request(string symbol, string token, string exchange = "NSE", StreamMode mode = StreamMode.Depth) => new(exchange, symbol, token, exchange, mode);

    [Fact]
    public void Fifty_one_instruments_go_in_two_requests_of_fifty_and_one()
    {
        var codec = new DhanDepth20FeedCodec();
        var messages = codec.Subscribe(Enumerable.Range(0, 51).Select(i => Request($"S{i}:20", (1000 + i).ToString())).ToList(), 0);
        Assert.Equal(2, messages.Count);
        Assert.Equal(50, System.Text.Json.Nodes.JsonNode.Parse(messages[0])!["InstrumentCount"]!.GetValue<int>());
        Assert.Equal(1, System.Text.Json.Nodes.JsonNode.Parse(messages[1])!["InstrumentCount"]!.GetValue<int>());
        Assert.Equal(2, codec.Resubscribe(0).Count);
    }

    [Fact]
    public void Letting_go_stops_the_instrument_with_code_24_and_only_in_depth_mode()
    {
        var codec = new DhanDepth20FeedCodec();
        codec.Subscribe([Request("SBIN:20", "3045")], 0);
        Assert.Empty(codec.Unsubscribe(new FeedTarget("NSE", "SBIN:20", "3045", "NSE"), StreamMode.Quote, 0));
        var stop = Assert.Single(codec.Unsubscribe(new FeedTarget("NSE", "SBIN:20", "3045", "NSE"), StreamMode.Depth, 0));
        var json = System.Text.Json.Nodes.JsonNode.Parse(stop)!;
        Assert.Equal(24, json["RequestCode"]!.GetValue<int>());
        Assert.Equal("3045", json["InstrumentList"]![0]!["SecurityId"]!.GetValue<string>());
        Assert.Empty(codec.Unsubscribe(new FeedTarget("NSE", "SBIN:20", "3045", "NSE"), null, 0));
        Assert.Empty(codec.Resubscribe(0));
    }

    [Fact]
    public void Only_nse_and_nfo_depth_requests_are_taken()
    {
        var codec = new DhanDepth20FeedCodec();
        Assert.Empty(codec.Subscribe([Request("A:20", "1", "BSE"), Request("B:20", "2", "NSE", StreamMode.Quote), Request("C:20", "", "NSE")], 0));
        Assert.Single(codec.Subscribe([Request("D:20", "4", "NFO")], 0));
    }
}
