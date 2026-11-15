using System.Net.WebSockets;
using System.Text;

namespace EPTradeBot.Infrastructure.Brokers.Tradejini;

/// <summary>Where Tradejini's quote and depth reads get a stream connection from: a short-lived one, opened for one read and closed after it.</summary>
public interface ITradejiniSockets
{
    /// <summary>A connection to the stream at this address, or null when it could not be made.</summary>
    Task<ITradejiniStream?> OpenAsync(Uri address, CancellationToken cancellationToken);
}

/// <summary>One stream connection: requests go out as text, binary frames come back.</summary>
public interface ITradejiniStream : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next binary frame, or null when none arrived within the time (text frames are passed over).</summary>
    Task<byte[]?> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The real connections (broker/tradejini api/data.py TradejiniWebSocket): a client socket to the stream address, ten seconds to connect. OpenAlgo waits three seconds after connecting before it asks; this one asks at once.</summary>
internal sealed class TradejiniSocketFactory : ITradejiniSockets
{
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(10);

    public async Task<ITradejiniStream?> OpenAsync(Uri address, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connecting.CancelAfter(ConnectWait);
            await socket.ConnectAsync(address, connecting.Token);
            return new Stream(socket);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException or IOException)
        {
            socket.Dispose();
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    private sealed class Stream(ClientWebSocket socket) : ITradejiniStream
    {
        private readonly byte[] buffer = new byte[64 * 1024];

        public async Task SendAsync(string text, CancellationToken cancellationToken) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

        public async Task<byte[]?> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open) return null;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waiting.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, waiting.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return null;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Binary) return message.ToArray();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }

            socket.Dispose();
        }
    }
}
