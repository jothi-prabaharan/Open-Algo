using EPTradeBot.Core.Brokers.Gtt;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Contract.Tests;

/// <summary>
/// Run evidence for the premises that `verification/tg7-equivalent-mutants.md` rests on: each test exercises the fact that makes a mutant impossible to see, over its whole domain where the domain is small.
/// </summary>
public class EquivalentMutantTests
{
    [Fact]
    public void A_refusal_status_of_at_least_400_is_kept_and_anything_else_is_400_whichever_way_the_edge_is_written()
    {
        for (var status = -10; status <= 1000; status++)
        {
            var original = status >= 400 ? status : 400;
            Assert.Equal(original, status > 400 ? status : 400);
            Assert.Equal(original, status >= 401 ? status : 400);
        }
    }

    [Fact]
    public void A_varint_shift_is_always_a_multiple_of_seven_so_the_bound_of_64_is_never_met()
    {
        for (var shift = 0; shift <= 140; shift += 7)
        {
            var original = shift < 64;
            Assert.Equal(original, shift <= 64);
            Assert.Equal(original, shift < 65);
        }
    }

    [Fact]
    public void A_fixed_width_read_ignores_the_bytes_after_its_width()
    {
        var random = new Random(7);
        for (var round = 0; round < 500; round++)
        {
            var bytes = new byte[8];
            random.NextBytes(bytes);
            Assert.Equal(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 5)));
            Assert.Equal(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 2)), System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 3)));
            var single = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0, 4));
            Assert.Equal(BitConverter.SingleToInt32Bits(single), BitConverter.SingleToInt32Bits(System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0, 5))));
        }
    }

    [Fact]
    public void A_net_short_position_has_sold_something_whatever_it_bought()
    {
        for (var buy = 0; buy <= 60; buy++)
            for (var sell = 0; sell <= 60; sell++)
                if (buy - sell < 0) Assert.True(sell > 0);
    }

    [Fact]
    public void A_tick_of_zero_gives_the_same_protected_price_as_no_tick()
    {
        foreach (var price in new[] { 0.5, 12.35, 805, 25000.45, 99999.99 })
            foreach (var action in new[] { "BUY", "SELL" })
                foreach (var type in new[] { "EQ", "FUT", "CE", "PE" })
                    Assert.Equal(MarketPriceProtection.ProtectedPrice(price, action, type, null), MarketPriceProtection.ProtectedPrice(price, action, type, 0.0));
    }

    [Fact]
    public void The_depth_of_an_instrument_always_has_five_bids_and_five_asks()
    {
        var depth = new MotilalDepth();
        Assert.Equal(5, depth.Bids.Length);
        Assert.Equal(5, depth.Asks.Length);
    }

    [Fact]
    public void Every_quote_the_book_holds_has_a_value_and_a_wire_byte_of_128_or_more_never_reaches_a_registered_instrument()
    {
        var book = new MotilalBook();
        book.Register("NSE", 5);
        byte[] Packet(char exchange, char kind)
        {
            var bytes = new byte[30];
            bytes[0] = (byte)exchange;
            BitConverter.GetBytes(5).CopyTo(bytes, 1);
            bytes[9] = (byte)kind;
            BitConverter.GetBytes(12.5f).CopyTo(bytes, 10);
            return bytes;
        }

        Assert.Null(book.GetQuote("NSE", 5));
        foreach (var kind in new[] { 'B', 'C', 'D', 'E', 'F', 'm', 'W', 'Z', '?' }) book.Feed(Packet('N', kind));
        var held = book.GetQuote("NSE", 5);
        Assert.True(held is null || held.Count > 0);
        foreach (var kind in new[] { 'A', 'G', 'H' })
        {
            var fresh = new MotilalBook();
            fresh.Feed(Packet('N', kind));
            Assert.True(fresh.GetQuote("NSE", 5) is { Count: > 0 });
        }

        foreach (var wire in new byte[] { 128, 129, 200, 255 })
        {
            var odd = new MotilalBook();
            var frame = Packet('N', 'A');
            frame[0] = wire;
            odd.Feed(frame);
            Assert.Null(odd.GetQuote("NSE", 5));
            Assert.True(odd.GetQuote("", 5) is { Count: > 0 });
        }
    }

    [Fact]
    public void Each_level_packet_fills_the_bid_and_the_ask_of_its_level_together()
    {
        var book = new MotilalBook();
        book.Register("NSE", 5);
        for (var level = 0; level < 5; level++)
        {
            var bytes = new byte[30];
            bytes[0] = (byte)'N';
            BitConverter.GetBytes(5).CopyTo(bytes, 1);
            bytes[9] = (byte)('B' + level);
            BitConverter.GetBytes(5f).CopyTo(bytes, 10);
            BitConverter.GetBytes(6f).CopyTo(bytes, 20);
            book.Feed(bytes);
            var depth = book.GetDepth("NSE", 5)!.Value;
            Assert.Equal(depth.Bids.Count(b => b is not null), depth.Asks.Count(a => a is not null));
        }
    }
}
