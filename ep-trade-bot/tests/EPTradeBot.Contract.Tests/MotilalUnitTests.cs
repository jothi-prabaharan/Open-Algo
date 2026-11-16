using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Motilal;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>Motilal's packets, store, codec and plugin called directly: the paths and boundaries the recorded cases do not reach.</summary>
public sealed class MotilalUnitTests
{
    private static byte[] Packet(char exchange, int scrip, char kind, byte[] body)
    {
        var bytes = new byte[30];
        bytes[0] = (byte)exchange;
        BitConverter.GetBytes(scrip).CopyTo(bytes, 1);
        bytes[9] = (byte)kind;
        body.CopyTo(bytes, 10);
        return bytes;
    }

    private static byte[] Floats(params float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    [Fact]
    public void The_login_packet_is_114_bytes_with_the_client_code_twice()
    {
        var packet = MotilalBook.LoginPacket("MO100");
        Assert.Equal(114, packet.Length);
        Assert.Equal((byte)'Q', packet[0]);
        Assert.Equal(111, BitConverter.ToUInt16(packet, 1));
        Assert.Equal(5, packet[3]);
        Assert.Equal("MO100          ", Encoding.ASCII.GetString(packet, 4, 15));
        Assert.Equal(5, packet[19]);
        Assert.Equal("MO100" + new string(' ', 25), Encoding.ASCII.GetString(packet, 20, 30));
        Assert.Equal(new byte[] { 1, 1, 1, 5 }, packet[50..54]);
        Assert.Equal("1.0.0     ", Encoding.ASCII.GetString(packet, 54, 10));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1 }, packet[64..69]);
        Assert.All(packet[69..], b => Assert.Equal((byte)' ', b));
    }

    [Theory]
    [InlineData("NSE", "CASH", 3045, true, "4407004e43e50b000001")]
    [InlineData("NSEFO", "DERIVATIVES", 49500, false, "4407004e445cc1000000")]
    [InlineData("BSEFO", "DERIVATIVES", 60001, true, "440700474461ea000001")]
    [InlineData("MCX", "DERIVATIVES", 80001, true, "4407004d448138010001")]
    public void A_register_packet_names_the_exchange_character_the_segment_and_the_scrip(string exchange, string segment, int scrip, bool add, string hex) =>
        Assert.Equal(hex, Convert.ToHexStringLower(MotilalBook.RegisterPacket(exchange, segment, scrip, add)));

    [Theory]
    [InlineData("NSE", "N")]
    [InlineData("NSEFO", "N")]
    [InlineData("BSE", "B")]
    [InlineData("MCX", "M")]
    [InlineData("NSECD", "C")]
    [InlineData("NCDEX", "D")]
    [InlineData("BSEFO", "G")]
    [InlineData("xyz", "X")]
    public void An_exchange_has_one_wire_character(string exchange, string expected) => Assert.Equal(expected, MotilalBook.WireChar(exchange));

    [Fact]
    public void A_packet_is_stored_under_the_exchange_its_scrip_was_registered_on()
    {
        var book = new MotilalBook();
        book.Register("NSEFO", 49500);
        book.Feed(Packet('N', 49500, 'A', Floats(24010.5f, 0, 0, 0, 0)));
        book.Feed(Packet('N', 3045, 'A', Floats(805.45f, 0, 0, 0, 0)));
        Assert.Equal(24010.5, book.GetQuote("NSEFO", 49500)!["ltp"]);
        Assert.Null(book.GetQuote("NSE", 49500));
        Assert.Equal(805.45, book.GetQuote("NSE", 3045)!["ltp"]);
    }

    [Fact]
    public void The_first_registration_of_a_wire_character_and_scrip_wins_and_unregistering_drops_the_data()
    {
        var book = new MotilalBook();
        book.Register("NSE", 7);
        book.Register("NSEFO", 7);
        book.Feed(Packet('N', 7, 'A', Floats(10.5f, 0, 0, 0, 0)));
        Assert.NotNull(book.GetQuote("NSE", 7));
        Assert.Null(book.GetQuote("NSEFO", 7));
        book.Unregister("NSEFO", 7);
        Assert.NotNull(book.GetQuote("NSE", 7));
        book.Unregister("NSE", 7);
        Assert.Null(book.GetQuote("NSE", 7));
        book.Feed(Packet('N', 7, 'A', Floats(11.5f, 0, 0, 0, 0)));
        Assert.Equal(11.5, book.GetQuote("NSE", 7)!["ltp"]);
    }

    [Fact]
    public void Packets_after_a_cut_short_one_and_a_negative_scrip_are_read()
    {
        var book = new MotilalBook();
        var frame = Packet('N', -5, 'A', Floats(1.25f, 0, 0, 0, 0)).Concat(Packet('N', 3045, 'A', Floats(2.5f, 0, 0, 0, 0))).Concat(new byte[10]).ToArray();
        Assert.Equal(2, book.Feed(frame).Count);
        Assert.Equal(1.25, book.GetQuote("NSE", -5)!["ltp"]);
        Assert.Equal(2.5, book.GetQuote("NSE", 3045)!["ltp"]);
    }

    [Fact]
    public void Open_interest_updates_a_quote_only_when_one_exists()
    {
        var book = new MotilalBook();
        var oi = BitConverter.GetBytes(7100).Concat(BitConverter.GetBytes(7500)).Concat(BitConverter.GetBytes(6500)).Concat(new byte[8]).ToArray();
        book.Feed(Packet('N', 9, 'm', oi));
        Assert.Null(book.GetQuote("NSE", 9));
        Assert.Equal(7100, book.GetOpenInterest("NSE", 9)!["open_interest"]);
        Assert.Equal(7500, book.GetOpenInterest("NSE", 9)!["oi_high"]);
        Assert.Equal(6500, book.GetOpenInterest("NSE", 9)!["oi_low"]);
        book.Feed(Packet('N', 9, 'A', Floats(1f, 0, 0, 0, 0)));
        Assert.Equal(0, book.GetQuote("NSE", 9)!["open_interest"]);
        book.Feed(Packet('N', 9, 'm', oi));
        Assert.Equal(7100, book.GetQuote("NSE", 9)!["oi"]);
    }

    [Fact]
    public void A_snapshot_needs_the_last_price_the_open_and_the_book_it_asks_for()
    {
        var book = new MotilalBook();
        void Level(int level, float bid, float ask) => book.Feed(Packet('N', 1, "BCDEF"[level], Floats(bid, 0, 0, ask, 0, 0).Take(0).Concat(BitConverter.GetBytes(bid)).Concat(BitConverter.GetBytes(100)).Concat(new byte[2]).Concat(BitConverter.GetBytes(ask)).Concat(BitConverter.GetBytes(110)).Concat(new byte[2]).ToArray()));
        Assert.False(book.HasSnapshot("NSE", 1, false, false));
        book.Feed(Packet('N', 1, 'A', Floats(5f, 0, 0, 0, 0)));
        Assert.False(book.HasSnapshot("NSE", 1, false, false));
        book.Feed(Packet('N', 1, 'G', Floats(1f, 2f, 0.5f, 1f).Concat(new byte[4]).ToArray()));
        Assert.False(book.HasSnapshot("NSE", 1, false, false));
        Level(0, 0f, 6f);
        Assert.False(book.HasSnapshot("NSE", 1, false, false));
        Level(0, 5f, 6f);
        Assert.True(book.HasSnapshot("NSE", 1, false, false));
        Assert.False(book.HasSnapshot("NSE", 1, false, true));
        Assert.False(book.HasSnapshot("NSE", 1, true, false));
        for (var i = 1; i < 5; i++) Level(i, 5f - i, 6f + i);
        Assert.True(book.HasSnapshot("NSE", 1, false, true));
        Assert.False(book.HasSnapshot("NSE", 1, true, true));
        book.Feed(Packet('N', 1, 'm', new byte[20]));
        Assert.True(book.HasSnapshot("NSE", 1, true, true));
    }

    [Fact]
    public void An_index_frame_is_the_text_the_python_client_sends()
    {
        Assert.Equal("{\"clientid\": \"MO100\", \"action\": \"IndexRegister\", \"exchange\": \"NSE\"}", MotilalBook.IndexFrame("MO100", "NSE", true));
        Assert.Equal("{\"clientid\": \"MO100\", \"action\": \"IndexUnregister\", \"exchange\": \"BSE\"}", MotilalBook.IndexFrame("MO100", "BSE", false));
    }

    // ---- the codec ---------------------------------------------------------------------------------------------

    private static FeedRequest Request(string symbol, string exchange, string token, string brokerExchange, StreamMode mode) => new(exchange, symbol, token, brokerExchange, mode);

    [Fact]
    public void A_subscription_is_held_until_the_server_has_answered_the_sign_in()
    {
        var codec = new MotilalFeedCodec();
        codec.Begin(new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
        Assert.Single(codec.Resubscribe(0));
        Assert.Empty(codec.Subscribe([Request("SBIN", "NSE", "3045", "NSE", StreamMode.Ltp), Request("NIFTY25NOV26FUT", "NFO", "49500", "NFO", StreamMode.Quote)], 0));
        codec.DecodeBinary(new byte[30], 0);
        Assert.Equal(["4407004e43e50b000001", "4407004e445cc1000001"], codec.TakeOutgoing());
        Assert.Empty(codec.TakeOutgoing());
        Assert.Equal(["4407004e43e50b000001"], codec.Subscribe([Request("SBIN", "NSE", "3045", "NSE", StreamMode.Depth)], 0));
    }

    [Fact]
    public void An_unsubscribe_with_no_mode_ends_every_mode_and_a_reconnect_starts_again()
    {
        var codec = new MotilalFeedCodec();
        codec.Begin(new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
        codec.Resubscribe(0);
        codec.DecodeBinary(new byte[30], 0);
        codec.Subscribe([Request("SBIN", "NSE", "3045", "NSE", StreamMode.Ltp), Request("SBIN", "NSE", "3045", "NSE", StreamMode.Quote)], 0);
        Assert.Equal(["4407004e43e50b000000"], codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0));
        Assert.Empty(codec.Resubscribe(0).Skip(1));
        codec.DecodeBinary(new byte[30], 0);
        Assert.Empty(codec.TakeOutgoing());
        codec.Reset();
        Assert.Empty(codec.DecodeBinary(Packet('N', 3045, 'A', Floats(1f, 0, 0, 0, 0)), 0));
    }

    [Fact]
    public void A_request_with_a_mode_out_of_range_or_a_token_that_is_not_a_number_is_left_out()
    {
        var codec = new MotilalFeedCodec();
        codec.Begin(new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
        codec.Resubscribe(0);
        codec.DecodeBinary(new byte[30], 0);
        Assert.Empty(codec.Subscribe([Request("SBIN", "NSE", "3045", "NSE", (StreamMode)0), Request("SBIN", "NSE", "3045", "NSE", (StreamMode)4), Request("SBIN", "NSE", "abc", "NSE", StreamMode.Ltp)], 0));
    }

    [Fact]
    public void A_derivative_exchange_uses_the_derivatives_segment_and_the_motilal_exchange_name()
    {
        var codec = new MotilalFeedCodec();
        codec.Begin(new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
        codec.Resubscribe(0);
        codec.DecodeBinary(new byte[30], 0);
        Assert.Equal(Convert.ToHexStringLower(MotilalBook.RegisterPacket("NSEFO", "DERIVATIVES", 1, true)), codec.Subscribe([Request("X", "NFO", "1", "NFO", StreamMode.Ltp)], 0).Single());
        Assert.Equal(Convert.ToHexStringLower(MotilalBook.RegisterPacket("BSEFO", "DERIVATIVES", 2, true)), codec.Subscribe([Request("Y", "BFO", "2", "BFO", StreamMode.Ltp)], 0).Single());
        Assert.Equal(Convert.ToHexStringLower(MotilalBook.RegisterPacket("NSECD", "DERIVATIVES", 3, true)), codec.Subscribe([Request("Z", "CDS", "3", "CDS", StreamMode.Ltp)], 0).Single());
        Assert.Equal(Convert.ToHexStringLower(MotilalBook.RegisterPacket("NCDEX", "DERIVATIVES", 4, true)), codec.Subscribe([Request("W", "NCX", "4", "NCX", StreamMode.Ltp)], 0).Single());
        Assert.Equal(Convert.ToHexStringLower(MotilalBook.RegisterPacket("MCX", "DERIVATIVES", 5, true)), codec.Subscribe([Request("V", "MCX", "5", "MCX", StreamMode.Ltp)], 0).Single());
        Assert.Equal(Convert.ToHexStringLower(MotilalBook.RegisterPacket("BSE", "CASH", 6, true)), codec.Subscribe([Request("U", "BSE", "6", "BSE", StreamMode.Ltp)], 0).Single());
    }

    // ---- the plugin --------------------------------------------------------------------------------------------

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master(Func<string, string, SymbolRecord?> find) : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult(find(symbol, exchange));

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }

    private sealed class Counting : IMotilalSockets
    {
        public int Runs { get; private set; }

        public List<int> Registered { get; } = [];

        public Task<bool> RunAsync(Uri address, byte[] login, IReadOnlyList<MotilalMessage> subscribes, IReadOnlyList<MotilalMessage> unsubscribes, Func<byte[], bool> onFrame, TimeSpan wait, CancellationToken cancellationToken)
        {
            Runs++;
            Registered.Add(subscribes.Count);
            return Task.FromResult(true);
        }
    }

    private static SymbolRecord Row(string symbol, string token, string exchange = "NSE") => new(symbol, symbol, exchange, exchange, token, symbol, null, null, 1, "EQ", 0.05);

    private static (MotilalPlugin Plugin, BrokerSession Session) Plugin(IMotilalSockets sockets)
    {
        var sessions = new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(new EPTradeBot.Core.Configuration.BrokerOptions()));
        return (new MotilalPlugin(null!, sessions, new Master((s, e) => Row(s, "1", e)), TimeProvider.System, sockets), new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
    }

    [Fact]
    public async Task More_than_a_hundred_quotes_are_read_a_hundred_at_a_time_and_an_empty_token_is_skipped()
    {
        var sockets = new Counting();
        var (plugin, session) = Plugin(sockets);
        var rows = Enumerable.Range(1, 101).Select(i => Row("S" + i, i.ToString())).ToList();
        var result = await plugin.GetMultiQuotesAsync(session, rows, CancellationToken.None);
        Assert.Equal(2, sockets.Runs);
        Assert.Equal([100, 1], sockets.Registered);
        Assert.Equal(101, result.Count);
        var skipped = await plugin.GetMultiQuotesAsync(session, [Row("EMPTY", "")], CancellationToken.None);
        Assert.Equal("Could not resolve token", skipped[0]!["error"]!.GetValue<string>());
        Assert.Null(skipped[0]!["data"]);
    }

    [Fact]
    public async Task A_connection_that_was_not_signed_in_is_an_error_that_says_what_to_do()
    {
        var failing = new Failing();
        var (plugin, session) = Plugin(failing);
        var depth = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(session, "SBIN", "NSE", CancellationToken.None));
        Assert.Equal(502, depth.HttpStatus);
        Assert.Contains("Try again in a moment", depth.Message);
        var many = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetMultiQuotesAsync(session, [Row("SBIN", "3045")], CancellationToken.None));
        Assert.Equal(502, many.HttpStatus);
    }

    private sealed class Failing : IMotilalSockets
    {
        public Task<bool> RunAsync(Uri address, byte[] login, IReadOnlyList<MotilalMessage> subscribes, IReadOnlyList<MotilalMessage> unsubscribes, Func<byte[], bool> onFrame, TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
