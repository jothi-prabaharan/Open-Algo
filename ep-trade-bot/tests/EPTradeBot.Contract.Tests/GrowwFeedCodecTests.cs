using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Contract.Tests;

/// <summary>The subscribe, unsubscribe and sign-in frames of Groww's NATS feed, held to what the Python client sends.</summary>
public class GrowwFeedCodecTests
{
    private static int counter;

    private static GrowwFeedCodec Codec(bool signedIn)
    {
        var token = "codec-" + Interlocked.Increment(ref counter);
        GrowwSocketCredentials.Put(token, "jwt", GrowwNkeys.EncodeSeed(new byte[32]));
        var codec = new GrowwFeedCodec();
        codec.Begin(new BrokerSession(Guid.Empty, "groww", token, null, null, new Dictionary<string, string>()));
        if (signedIn)
        {
            codec.DecodeText("INFO {\"nonce\":\"abc\"}\r\n", 0);
            codec.TakeOutgoing();
            codec.DecodeText("PONG\r\n", 0);
        }

        return codec;
    }

    private static FeedRequest Ask(string exchange, string symbol, string token, StreamMode mode) => new(exchange, symbol, token, exchange, mode);

    [Fact]
    public void A_signed_in_connection_numbers_its_subscriptions_from_one()
    {
        var codec = Codec(true);
        Assert.Equal(["SUB /ld/eq/nse/price.3045 1\r\n", "PING\r\n"], codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp)], 0));
        Assert.Equal(["SUB /ld/eq/nse/price.1594 2\r\n", "PING\r\n"], codec.Subscribe([Ask("NSE", "INFY", "1594", StreamMode.Ltp)], 0));
    }

    [Fact]
    public void A_depth_subscription_also_takes_the_price_and_an_index_takes_the_price_only()
    {
        var codec = Codec(true);
        Assert.Equal(["SUB /ld/eq/nse/book.3045 1\r\n", "SUB /ld/eq/nse/price.3045 2\r\n", "PING\r\n"], codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0));
        Assert.Equal(["SUB /ld/eq/nse/price.NIFTY 3\r\n", "PING\r\n"], codec.Subscribe([Ask("NSE_INDEX", "NIFTY", "26000", StreamMode.Depth)], 0));
    }

    [Fact]
    public void What_was_asked_before_sign_in_goes_out_when_the_server_accepts_the_connection_and_a_plain_ok_is_enough()
    {
        var codec = Codec(false);
        Assert.Empty(codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp)], 0));
        codec.DecodeText("+OK\r\n", 0);
        Assert.Equal(["SUB /ld/eq/nse/price.3045 1\r\n", "PING\r\n"], codec.TakeOutgoing());
    }

    [Fact]
    public void A_pong_is_enough_only_once_the_connection_message_was_sent()
    {
        var codec = Codec(false);
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp)], 0);
        codec.DecodeText("PONG\r\n", 0);
        Assert.Empty(codec.TakeOutgoing());
        codec.DecodeText("INFO {\"nonce\":\"abc\"}\r\n", 0);
        codec.TakeOutgoing();
        codec.DecodeText("PONG\r\n", 0);
        Assert.Equal(["SUB /ld/eq/nse/price.3045 1\r\n", "PING\r\n"], codec.TakeOutgoing());
    }

    [Fact]
    public void Without_a_mode_an_unsubscribe_ends_the_price_quote_and_depth_subscriptions_of_the_instrument()
    {
        var codec = Codec(true);
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp), Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0);
        var frames = codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0);
        Assert.Equal(frames.Count, frames.Distinct().Count());
        Assert.All(frames, f => Assert.StartsWith("UNSUB ", f));
        Assert.Empty(codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0));
        // The price subscription asked for twice (as Ltp and as the depth's shadow) is two ids, and both are ended.
        var again = Codec(true);
        again.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Quote)], 0);
        Assert.Single(again.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0));
        var third = Codec(true);
        third.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0);
        Assert.Equal(2, third.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), StreamMode.Depth, 0).Count);
    }

    [Fact]
    public void An_authorization_refusal_signs_the_connection_out_and_another_refusal_does_not()
    {
        var other = Codec(true);
        other.DecodeText("-ERR 'Maximum Connections Exceeded'\r\n", 0);
        Assert.NotEmpty(other.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp)], 0));
        foreach (var refusal in new[] { "-ERR 'Authorization Violation'\r\n", "-ERR 'Authentication Timeout'\r\n" })
        {
            var codec = Codec(true);
            codec.DecodeText(refusal, 0);
            Assert.Empty(codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp)], 0));
        }
    }
}
