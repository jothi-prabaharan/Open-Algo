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

    // ---- frames the server sends ---------------------------------------------------------------------------------

    private static byte[] Double(double value) => BitConverter.GetBytes(value);

    private static byte[] Field(int number, int wire, params byte[] body) => [.. Varint((ulong)((number << 3) | wire)), .. body];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0) b |= 0x80;
            bytes.Add(b);
        }
        while (value != 0);
        return [.. bytes];
    }

    private static byte[] Inside(int number, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. Field(number, 2), .. Varint((ulong)body.Length), .. body];
    }

    private static byte[] Message(string subject, int sid, byte[] payload, bool trailer = true) =>
        [.. System.Text.Encoding.UTF8.GetBytes($"MSG {subject} {sid} {payload.Length}\r\n"), .. payload, .. (trailer ? System.Text.Encoding.UTF8.GetBytes("\r\n") : [])];

    private static byte[] Level(double price, long quantity, int side = 2) => Inside(side, Field(1, 0, 1), Inside(2, Field(1, 1, Double(price)), Field(2, 1, Double(quantity))));

    [Fact]
    public void A_price_tick_is_published_with_its_fields_and_unknown_fields_of_every_kind_are_stepped_over()
    {
        var codec = Codec(true);
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Quote)], 0);
        byte[] payload =
            [
                .. Field(1, 0, 5),
                .. Field(2, 0, 1),
                .. Inside(4, Field(1, 1, Double(1700000000)), Field(9, 5, 1, 2, 3, 4), Field(8, 1, Double(7)), Field(12, 2, 2, 9, 9), Field(13, 1, Double(812.5)), Field(3, 1, Double(815)), Field(7, 1, Double(999))),
            ];
        var published = codec.DecodeBinary(Message("/ld/eq/nse/price.3045", 1, payload), 5);
        var tick = Assert.Single(published);
        Assert.Equal("NSE_SBIN_QUOTE", tick.Topic);
        Assert.Equal(812.5, tick.Data["ltp"]!.GetValue<double>());
        Assert.Equal(815.0, tick.Data["high"]!.GetValue<double>());
        Assert.Equal(1700000000.0, tick.Data["ltt"]!.GetValue<double>());
        Assert.Equal(2, tick.Data["mode"]!.GetValue<int>());
        Assert.Equal(0.0, tick.Data["open"]!.GetValue<double>());
    }

    [Fact]
    public void A_message_with_no_closing_line_end_is_still_read()
    {
        var codec = Codec(true);
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp)], 0);
        var payload = Inside(4, Field(13, 1, Double(800)));
        Assert.Single(codec.DecodeBinary(Message("/ld/eq/nse/price.3045", 1, payload, trailer: false), 0));
    }

    [Fact]
    public void An_index_asked_for_depth_is_published_as_a_price()
    {
        var codec = Codec(true);
        codec.Subscribe([Ask("NSE_INDEX", "NIFTY", "26000", StreamMode.Depth)], 0);
        var payload = Inside(6, Field(1, 1, Double(1)), Field(2, 1, Double(25000)));
        var published = codec.DecodeBinary(Message("/ld/eq/nse/price.NIFTY", 1, payload), 0);
        var tick = Assert.Single(published);
        Assert.Equal("NSE_INDEX_NIFTY_LTP", tick.Topic);
        Assert.Equal(1, tick.Data["mode"]!.GetValue<int>());
    }

    [Fact]
    public void A_depth_tick_reads_five_levels_and_drops_the_ones_with_neither_a_price_nor_a_quantity()
    {
        var codec = Codec(true);
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0);
        var depth = Inside(5, Field(1, 1, Double(1)), Level(10, 1), Level(0, 7), Level(12, 0), Level(0, 0), Level(9, 2), Level(8, 3), Level(7, 4), Level(11, 5, 3));
        var published = codec.DecodeBinary(Message("/ld/eq/nse/book.3045", 1, depth), 0);
        var tick = Assert.Single(published);
        Assert.Equal(4, tick.Data["depth"]!["buy"]!.AsArray().Count);
        Assert.Single(tick.Data["depth"]!["sell"]!.AsArray());
        Assert.Equal(10.0, tick.Data["bid"]!.GetValue<double>());
        Assert.Equal(11.0, tick.Data["ask"]!.GetValue<double>());
    }

    [Fact]
    public void What_was_held_for_an_instrument_is_forgotten_when_its_last_subscription_ends()
    {
        var codec = Codec(true);
        var who = new FeedTarget("NSE", "SBIN", "3045", "NSE");
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0);
        codec.DecodeBinary(Message("/ld/eq/nse/book.3045", 1, Inside(5, Field(1, 1, Double(1)), Level(10, 1))), 0);
        codec.Unsubscribe(who, null, 0);
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0);
        var published = codec.DecodeBinary(Message("/ld/eq/nse/price.3045", 3, Inside(4, Field(13, 1, Double(800)))), 0);
        var tick = Assert.Single(published);
        Assert.Empty(tick.Data["depth"]!["buy"]!.AsArray());
    }

    [Fact]
    public void An_unsubscribe_without_a_mode_ends_each_mode_asked_for_and_one_still_held_keeps_the_instrument()
    {
        var codec = Codec(true);
        var who = new FeedTarget("NSE", "SBIN", "3045", "NSE");
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp), Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0);
        Assert.Equal(3, codec.Unsubscribe(who, null, 0).Count);
        codec.Subscribe([Ask("NSE", "SBIN", "3045", StreamMode.Ltp), Ask("NSE", "SBIN", "3045", StreamMode.Depth)], 0);
        codec.DecodeBinary(Message("/ld/eq/nse/book.3045", 7, Inside(5, Field(1, 1, Double(1)), Level(10, 1))), 0);
        codec.Unsubscribe(who, StreamMode.Ltp, 0);
        var published = codec.DecodeBinary(Message("/ld/eq/nse/price.3045", 8, Inside(4, Field(13, 1, Double(800)))), 0);
        Assert.Single(published);
        Assert.Single(published[0].Data["depth"]!["buy"]!.AsArray());
    }
}
