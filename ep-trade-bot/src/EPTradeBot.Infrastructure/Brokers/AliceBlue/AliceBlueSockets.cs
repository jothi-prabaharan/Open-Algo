using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.AliceBlue;

/// <summary>Where AliceBlue's quote and depth reads get a connected market-data socket from. One socket serves every read of a session; a read that finds it unusable asks for a new one.</summary>
public interface IAliceBlueSockets
{
    /// <summary>The socket for this session, connected and signed in, or null when one could not be made. <paramref name="forceNew"/> replaces the one held.</summary>
    Task<AliceBlueMarketSocket?> GetAsync(BrokerSession session, bool forceNew, CancellationToken cancellationToken);
}

/// <summary>
/// The real sockets (broker/aliceblue api/data.py get_websocket): a socket session is asked for over REST, then the first server (the second when that fails) is connected and signed in; a socket is reused while it has heard from the
/// server within a minute, and it is given up when nobody has used it for ten minutes. A new session replaces the old one's.
/// </summary>
internal sealed class AliceBlueSocketPool(Func<BrokerSession, CancellationToken, Task<bool>> createSession, Func<string?> configuredUrl, TimeProvider clock) : IAliceBlueSockets
{
    private static readonly string[] Servers = ["wss://ws1.aliceblueonline.com/NorenWS/", "wss://ws2.aliceblueonline.com/NorenWS/"];
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim building = new(1, 1);
    private Entry? current;

    private sealed class Entry(string session, ClientWebSocket socket, AliceBlueMarketSocket market)
    {
        public string Session { get; } = session;

        public ClientWebSocket Socket { get; } = socket;

        public AliceBlueMarketSocket Market { get; } = market;

        public CancellationTokenSource Stop { get; } = new();

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public DateTimeOffset LastMessage { get; set; }

        public DateTimeOffset LastUse { get; set; }
    }

    private bool Usable(Entry? entry, BrokerSession session) =>
        entry is not null && entry.Session == session.AuthToken && entry.Market.Connected && entry.Socket.State == WebSocketState.Open && clock.GetUtcNow() - entry.LastMessage < Fresh;

    public async Task<AliceBlueMarketSocket?> GetAsync(BrokerSession session, bool forceNew, CancellationToken cancellationToken)
    {
        var held = current;
        if (!forceNew && Usable(held, session))
        {
            held!.LastUse = clock.GetUtcNow();
            return held.Market;
        }

        await building.WaitAsync(cancellationToken);
        try
        {
            // A caller that waited for the lock takes what the one before it built, unless it asked for a new one on purpose after that.
            if (!forceNew && Usable(current, session))
            {
                current!.LastUse = clock.GetUtcNow();
                return current.Market;
            }

            if (current is not null) Close(current);
            current = null;
            current = await BuildAsync(session, cancellationToken);
            return current?.Market;
        }
        finally
        {
            building.Release();
        }
    }

    private static void Close(Entry entry)
    {
        entry.Market.Connected = false;
        entry.Stop.Cancel();
        try
        {
            entry.Socket.Abort();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or WebSocketException)
        {
        }
    }

    private async Task<Entry?> BuildAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ClientId) || string.IsNullOrWhiteSpace(session.AuthToken)) return null;
        if (!await createSession(session, cancellationToken)) return null;
        var urls = configuredUrl() is { } one ? [one] : Servers;
        foreach (var url in urls)
        {
            var socket = new ClientWebSocket();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(new Uri(url), timeout.Token);
                Entry? entry = null;
                var market = new AliceBlueMarketSocket(text => SendNow(entry!, text));
                entry = new Entry(session.AuthToken, socket, market) { LastMessage = clock.GetUtcNow(), LastUse = clock.GetUtcNow() };
                _ = ReadAsync(entry);
                _ = HeartbeatAsync(entry);
                var inner = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.AuthToken)));
                var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inner)));
                SendNow(entry, new JsonObject { ["susertoken"] = token, ["t"] = "c", ["actid"] = $"{session.ClientId}_API", ["uid"] = $"{session.ClientId}_API", ["source"] = "API" }.ToJsonString());
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!market.Connected && DateTime.UtcNow < deadline && socket.State == WebSocketState.Open) await Task.Delay(50, cancellationToken);
                if (market.Connected) return entry;
                Close(entry);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException or ObjectDisposedException)
            {
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested) throw;
            }
        }

        return null;
    }

    private static void SendNow(Entry entry, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        entry.SendLock.Wait();
        try
        {
            entry.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, entry.Stop.Token).GetAwaiter().GetResult();
        }
        finally
        {
            entry.SendLock.Release();
        }
    }

    private async Task ReadAsync(Entry entry)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!entry.Stop.IsCancellationRequested && entry.Socket.State == WebSocketState.Open)
            {
                var result = await entry.Socket.ReceiveAsync(buffer, entry.Stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                entry.LastMessage = clock.GetUtcNow();
                if (result.MessageType == WebSocketMessageType.Text) entry.Market.OnMessage(text);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            entry.Market.Connected = false;
        }
    }

    /// <summary>A frame every fifty seconds keeps the socket open through a quiet spell; the socket is given up once nobody has used it for ten minutes.</summary>
    private async Task HeartbeatAsync(Entry entry)
    {
        try
        {
            while (!entry.Stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(50), entry.Stop.Token);
                if (clock.GetUtcNow() - entry.LastUse > Idle)
                {
                    Close(entry);
                    return;
                }

                SendNow(entry, "{\"k\":\"\",\"t\":\"h\"}");
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
