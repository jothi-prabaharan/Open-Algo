using System.Net.WebSockets;
using System.Text;

namespace EPTradeBot.Infrastructure.Brokers.Pocketful;

/// <summary>A frame of Pocketful's feed: binary, or text.</summary>
public sealed record PocketfulFrame(bool Binary, byte[] Bytes, string Text)
{
    public static PocketfulFrame Of(byte[] bytes) => new(true, bytes, "");

    public static PocketfulFrame Of(string text) => new(false, [], text);
}

/// <summary>Where Pocketful's quote, depth and several-quotes reads get their answers: a short-lived feed connection.</summary>
public interface IPocketfulSockets
{
    /// <summary>
    /// Connects to <paramref name="address"/>, sends each subscribe text, and hands every frame to <paramref name="onFrame"/> until it says it has what it needs or <paramref name="wait"/> is up; then sends each unsubscribe text and closes.
    /// False when the connection could not be opened (nothing was sent).
    /// </summary>
    Task<bool> RunAsync(Uri address, IReadOnlyList<string> subscribes, IReadOnlyList<string> unsubscribes, Func<PocketfulFrame, bool> onFrame, TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>
/// The real connection (broker/pocketful api/pocketfulwebsocket.py). OpenAlgo opens one feed socket for the whole process, waits up to five seconds for it and keeps it, with a heartbeat thread and a subscriber count; a read here has a socket of its own, which is opened for it, always closed after it
/// and given five seconds to open. Every path closes it.
/// </summary>
internal sealed class PocketfulSnapshotSocket : IPocketfulSockets
{
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(5);

    public async Task<bool> RunAsync(Uri address, IReadOnlyList<string> subscribes, IReadOnlyList<string> unsubscribes, Func<PocketfulFrame, bool> onFrame, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        try
        {
            try
            {
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connecting.CancelAfter(Connect);
                await socket.ConnectAsync(address, connecting.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                return false;
            }

            try
            {
                foreach (var text in subscribes) await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(wait);
                var buffer = new byte[16 * 1024];
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, deadline.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return true;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var frame = result.MessageType == WebSocketMessageType.Binary ? PocketfulFrame.Of(message.ToArray()) : PocketfulFrame.Of(Encoding.UTF8.GetString(message.ToArray()));
                    if (onFrame(frame)) break;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The time is up, or the connection dropped: what was read stands.
                if (cancellationToken.IsCancellationRequested) throw;
            }

            try
            {
                if (socket.State == WebSocketState.Open)
                    foreach (var text in unsubscribes) await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or IOException)
            {
            }

            return true;
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
