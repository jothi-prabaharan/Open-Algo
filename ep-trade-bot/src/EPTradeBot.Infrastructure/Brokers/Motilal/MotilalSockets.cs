using System.Net.WebSockets;
using System.Text;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

/// <summary>A message to send on Motilal's feed: a binary packet (login, register) or a text frame (the index requests).</summary>
public sealed record MotilalMessage(bool Binary, byte[] Bytes, string Text)
{
    public static MotilalMessage Of(byte[] bytes) => new(true, bytes, "");

    public static MotilalMessage Of(string text) => new(false, [], text);
}

/// <summary>Where Motilal's depth and several-quotes reads get their answers: a short-lived connection to the broadcast feed.</summary>
public interface IMotilalSockets
{
    /// <summary>
    /// Connects to <paramref name="address"/>, sends the binary <paramref name="login"/> and waits for the first frame (the server's answer, which marks the connection as signed in). Then sends each of <paramref name="subscribes"/> and hands every frame
    /// (the first one too) to <paramref name="onFrame"/> until it says it has what it needs or <paramref name="wait"/> is up, and finally sends each of <paramref name="unsubscribes"/>. False when the connection was not signed in within the connect
    /// time (nothing was subscribed).
    /// </summary>
    Task<bool> RunAsync(Uri address, byte[] login, IReadOnlyList<MotilalMessage> subscribes, IReadOnlyList<MotilalMessage> unsubscribes, Func<byte[], bool> onFrame, TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>
/// The real connection (broker/motilal api/motilal_websocket.py). OpenAlgo keeps one such socket for the session and waits ten seconds for it to be signed in; here each read opens its own, which is closed on every path. The certificate is checked, where
/// OpenAlgo switches the check off.
/// </summary>
public sealed class MotilalSnapshotSocket : IMotilalSockets
{
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(10);

    public async Task<bool> RunAsync(Uri address, byte[] login, IReadOnlyList<MotilalMessage> subscribes, IReadOnlyList<MotilalMessage> unsubscribes, Func<byte[], bool> onFrame, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connecting.CancelAfter(Connect);
            try
            {
                await socket.ConnectAsync(address, connecting.Token);
                await Send(socket, MotilalMessage.Of(login), connecting.Token);
                var ack = await ReceiveAsync(socket, connecting.Token);
                if (ack is null) return false;
                onFrame(ack);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                return false;
            }

            try
            {
                foreach (var message in subscribes) await Send(socket, message, cancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(wait);
                while (socket.State == WebSocketState.Open)
                {
                    var frame = await ReceiveAsync(socket, deadline.Token);
                    if (frame is null || onFrame(frame)) break;
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
                    foreach (var message in unsubscribes) await Send(socket, message, CancellationToken.None);
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

    private static Task Send(ClientWebSocket socket, MotilalMessage message, CancellationToken cancellationToken) =>
        socket.SendAsync(message.Binary ? message.Bytes : Encoding.UTF8.GetBytes(message.Text), message.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>The next binary frame, or null when the server closed. A text frame (an error notice) is passed over.</summary>
    private static async Task<byte[]?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Binary) return message.ToArray();
        }
    }
}
