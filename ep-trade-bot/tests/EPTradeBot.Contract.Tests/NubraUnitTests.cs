using System.Net;
using System.Net.WebSockets;
using System.Text;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Nubra;
using Xunit;

namespace EPTradeBot.Contract.Tests;

public sealed class NubraUnitTests
{
    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        while (v >= 0x80)
        {
            bytes.Add((byte)(v | 0x80));
            v >>= 7;
        }

        bytes.Add((byte)v);
        return [.. bytes];
    }

    private static byte[] Field(int number, byte[] payload) => [.. Varint((ulong)(number << 3 | 2)), .. Varint((ulong)payload.Length), .. payload];

    private static byte[] Number(int number, ulong value) => [.. Varint((ulong)(number << 3)), .. Varint(value)];

    private static byte[] Any(string url, byte[] value) => [.. Field(1, Encoding.UTF8.GetBytes(url)), .. Field(2, value)];

    private static byte[] Frame(string type, byte[] message) => Any("type.googleapis.com/x", Any($"type.googleapis.com/{type}", message));

    [Fact]
    public void A_frame_that_is_cut_short_reads_as_empty()
    {
        var whole = Frame("BatchWebSocketOrderbookMessage", Field(2, [.. Number(8, 3045), .. Number(5, 80545)]));
        for (var cut = 1; cut < whole.Length; cut++)
        {
            var frame = NubraFrames.Decode(whole.AsSpan(0, cut));
            Assert.True(frame.Books.Count() <= 1);
        }

        Assert.Single(NubraFrames.Decode(whole).Books);
    }

    [Fact]
    public void A_varint_that_never_ends_reads_as_empty()
    {
        var frame = NubraFrames.Decode(Enumerable.Repeat((byte)0xff, 20).ToArray());
        Assert.Empty(frame.Books);
        Assert.Empty(frame.Indexes);
    }

    [Fact]
    public void Fields_the_reader_does_not_know_are_skipped_whatever_their_kind()
    {
        byte[] extra = [.. Number(30, 5), .. Field(31, [1, 2, 3]), .. Varint(32 << 3 | 5), 0, 0, 0, 0, .. Varint(33 << 3 | 1), 0, 0, 0, 0, 0, 0, 0, 0];
        var frame = NubraFrames.Decode(Frame("BatchWebSocketOrderbookMessage", Field(2, [.. extra, .. Number(8, 3045), .. Number(5, 80545)])));
        var book = Assert.Single(frame.Books);
        Assert.Equal(3045, book.RefId);
        Assert.Equal(80545, book.Ltp);
    }

    [Fact]
    public void A_wire_type_that_does_not_exist_reads_as_empty()
    {
        var frame = NubraFrames.Decode(Frame("BatchWebSocketOrderbookMessage", Field(2, [.. Varint(9 << 3 | 7), 1])));
        Assert.Empty(frame.Books);
    }

    [Fact]
    public void A_message_of_a_kind_nobody_reads_is_empty()
    {
        var frame = NubraFrames.Decode(Frame("BatchWebSocketSomethingElse", Field(2, Number(8, 3045))));
        Assert.Empty(frame.Books);
        Assert.Empty(frame.Greeks);
    }

    [Fact]
    public void Each_kind_of_message_is_read_from_its_own_fields()
    {
        var index = Frame("BatchWebSocketIndexMessage", [.. Field(2, [.. Field(1, "NIFTY 50"u8.ToArray()), .. Number(3, 2410025), .. Number(10 , 0)]), .. Field(3, Field(1, "SBIN"u8.ToArray()))]);
        var decoded = NubraFrames.Decode(index);
        Assert.Equal(["NIFTY 50"], decoded.Listed.Select(i => i.Name));
        Assert.Equal(["SBIN"], decoded.Instruments.Select(i => i.Name));
        Assert.Equal(2410025, decoded.Listed[0].Value);

        var bucket = NubraFrames.Decode(Frame("BatchWebSocketIndexBucketMessage", Field(3, [.. Field(1, "SBIN"u8.ToArray()), .. Number(5, 10), .. Number(8, 20), .. Number(9, 3), .. Number(11, 4)])));
        var b = Assert.Single(bucket.InstrumentBuckets);
        Assert.Equal((10, 20, 3, 4), (b.Open, b.Close, b.BucketVolume, b.CumulativeVolume));

        var greeks = NubraFrames.Decode(Frame("BatchWebSocketGreeksMessage", Field(2, [.. Number(14, 3045), .. Number(12, 7), .. Number(15, 6), .. Number(5, 9), .. Number(13, 8)])));
        var g = Assert.Single(greeks.Greeks);
        Assert.Equal((3045, 7, 6, 9, 8), (g.RefId, g.Oi, g.PrevOi, g.Ltp, g.Volume));
    }

    [Fact]
    public void The_feed_address_carries_the_token_and_the_device_and_a_session_without_a_token_has_none()
    {
        var plugin = new NubraPlugin(null!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(new EPTradeBot.Core.Configuration.BrokerOptions())), null!, TimeProvider.System);
        var endpoint = plugin.FeedEndpoint(new BrokerSession(Guid.Empty, "nubra", "tok", null, null, new Dictionary<string, string>()), null);
        Assert.Equal("wss://api.nubra.io/apibatch/ws", endpoint!.Url.OriginalString);
        Assert.Equal("Bearer tok", endpoint.Headers["Authorization"]);
        Assert.Equal("OPENALGO", endpoint.Headers["x-device-id"]);
        Assert.Equal(new Uri("wss://example.test/ws"), plugin.FeedEndpoint(new BrokerSession(Guid.Empty, "nubra", "tok", null, null, new Dictionary<string, string>()), "wss://example.test/ws")!.Url);
        Assert.Null(plugin.FeedEndpoint(new BrokerSession(Guid.Empty, "nubra", " ", null, null, new Dictionary<string, string>()), null));
        Assert.Null(plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(20), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public async Task The_real_connection_sends_its_headers_hands_back_frames_and_sends_commands()
    {
        var port = Random.Shared.Next(20000, 40000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        string? auth = null, device = null, received = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            auth = context.Request.Headers["Authorization"];
            device = context.Request.Headers["x-device-id"];
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            received = Encoding.UTF8.GetString(buffer, 0, result.Count);
            await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.SendAsync("text is not read"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.SendAsync(new byte[] { 4 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            await Task.Delay(300);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });

        var sockets = new NubraSnapshotSocket();
        await using var link = await sockets.OpenAsync(new Uri($"ws://localhost:{port}/"), "tok", "OPENALGO", CancellationToken.None);
        Assert.NotNull(link);
        Assert.True(await link!.SendAsync("batch_subscribe tok index {}", CancellationToken.None));
        var frames = new List<byte[]>();
        for (var i = 0; i < 6; i++) frames.AddRange(await link.TickAsync(CancellationToken.None));
        await server;
        Assert.Equal("Bearer tok", auth);
        Assert.Equal("OPENALGO", device);
        Assert.Equal("batch_subscribe tok index {}", received);
        Assert.Equal([[1, 2, 3], [4]], frames);
        // After the server has closed, a tick still takes its time and gives nothing, and a command cannot be sent.
        Assert.Empty(await link.TickAsync(CancellationToken.None));
        Assert.False(await link.SendAsync("late", CancellationToken.None));
    }

    [Fact]
    public async Task A_connection_that_cannot_be_opened_is_none()
    {
        var sockets = new NubraSnapshotSocket();
        Assert.Null(await sockets.OpenAsync(new Uri("ws://localhost:1/"), "tok", "OPENALGO", CancellationToken.None));
    }
}
