using System.Net.WebSockets;
using System.Text;

namespace EPTradeBot.Infrastructure.Brokers.Mstock;

/// <summary>Where mStock's depth read gets its answer: a short-lived market-data connection that signs in, asks for one token and hands back the first frame the caller accepts.</summary>
public interface IMstockSockets
{
    /// <summary>
    /// Connects to <paramref name="address"/>, sends the sign-in text and then the subscribe frame, and reads frames until <paramref name="accept"/> takes one of the binary ones (or the frames run out, or a read fails).
    /// Returns the accepted frame, or null when none was.
    /// </summary>
    Task<byte[]?> SnapshotAsync(Uri address, string login, string subscribe, Func<byte[], bool> accept, CancellationToken cancellationToken);
}

/// <summary>
/// The real connection (broker/mstock api/mstockwebsocket.py fetch_quote). OpenAlgo reads once for an answer to the sign-in before it subscribes, and mStock sends none, so each depth read there waits ten seconds for nothing; this one
/// sends the subscribe straight after the sign-in. It reads up to four frames after that (a late answer to the sign-in would be one of them) with ten seconds for each, and closes the connection on every path.
/// </summary>
internal sealed class MstockSnapshotSocket : IMstockSockets
{
    private const int Frames = 4;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public async Task<byte[]?> SnapshotAsync(Uri address, string login, string subscribe, Func<byte[], bool> accept, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connecting.CancelAfter(Wait);
                await socket.ConnectAsync(address, connecting.Token);
            }

            await socket.SendAsync(Encoding.UTF8.GetBytes(login), WebSocketMessageType.Text, true, cancellationToken);
            await socket.SendAsync(Encoding.UTF8.GetBytes(subscribe), WebSocketMessageType.Text, true, cancellationToken);
            var buffer = new byte[16 * 1024];
            for (var i = 0; i < Frames; i++)
            {
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                reading.CancelAfter(Wait);
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, reading.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary) continue;
                var frame = message.ToArray();
                if (frame.Length > 0 && accept(frame)) return frame;
            }

            return null;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException or ObjectDisposedException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
        finally
        {
            try
            {
                if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }
}
