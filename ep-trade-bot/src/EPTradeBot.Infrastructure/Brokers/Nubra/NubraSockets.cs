using System.Net.WebSockets;
using System.Text;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>An open feed connection of one read: text commands go out, binary frames come back a tick at a time.</summary>
public interface INubraConnection : IAsyncDisposable
{
    /// <summary>Sends a command; false when it could not be sent (the connection is down).</summary>
    Task<bool> SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>Waits one tick (about fifty milliseconds) and hands back the binary frames that arrived in it, none when none did.</summary>
    Task<IReadOnlyList<byte[]>> TickAsync(CancellationToken cancellationToken);
}

/// <summary>Where Nubra's quote, depth and several-quotes reads get their frames: a short-lived feed connection.</summary>
public interface INubraSockets
{
    /// <summary>Opens a connection to <paramref name="address"/> with the session token as a bearer token and the device id; null when it did not open.</summary>
    Task<INubraConnection?> OpenAsync(Uri address, string token, string deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// The real connection (broker/nubra/api/nubrawebsocket.py). OpenAlgo opens one for the read, waits up to ten seconds for it and closes it when the reader is let go; here a read has a connection of its own, opened for it and always closed after it. Text
/// answers from Nubra are not read.
/// </summary>
public sealed class NubraSnapshotSocket : INubraSockets
{
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(10);

    public async Task<INubraConnection?> OpenAsync(Uri address, string token, string deviceId, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        socket.Options.SetRequestHeader("x-device-id", deviceId);
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connecting.CancelAfter(Connect);
            await socket.ConnectAsync(address, connecting.Token);
            return new Connection(socket);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException)
        {
            socket.Dispose();
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    private sealed class Connection(ClientWebSocket socket) : INubraConnection
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);
        private Task<(byte[]? Binary, bool Closed)>? pending;

        public async Task<bool> SendAsync(string text, CancellationToken cancellationToken)
        {
            try
            {
                if (socket.State != WebSocketState.Open) return false;
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
            {
                return false;
            }
        }

        private async Task<(byte[]? Binary, bool Closed)> ReadOneAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return (null, true);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                return (result.MessageType == WebSocketMessageType.Binary ? message.ToArray() : null, false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                return (null, true);
            }
        }

        public async Task<IReadOnlyList<byte[]>> TickAsync(CancellationToken cancellationToken)
        {
            var frames = new List<byte[]>();
            var delay = Task.Delay(Tick, cancellationToken);
            while (true)
            {
                pending ??= ReadOneAsync();
                var done = await Task.WhenAny(pending, delay);
                if (done == delay) break;
                var (binary, closed) = await pending;
                pending = null;
                if (closed)
                {
                    await delay;
                    break;
                }

                if (binary is not null) frames.Add(binary);
            }

            await delay;
            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or IOException)
            {
            }

            socket.Abort();
            if (pending is not null)
            {
                try
                {
                    await pending;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            }

            socket.Dispose();
        }
    }
}
