using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.IntegrationTests;

/// <summary>A broker's market-data endpoint on a real loopback port: records who connected and what they said, and sends whatever a test wants.</summary>
public sealed class FakeFeedServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly ConcurrentQueue<WebSocket> sockets = new();
    private readonly ConcurrentQueue<string> received = new();

    public FakeFeedServer(HttpStatusCode? refuseWith = null)
    {
        RefuseWith = refuseWith;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        app = builder.Build();
        app.UseWebSockets();
        app.Map("/{**path}", async (HttpContext context) =>
        {
            Requests.Enqueue(new Connected(context.Request.Path.Value ?? "", context.Request.QueryString.Value ?? "", context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            if (RefuseWith is { } status)
            {
                context.Response.StatusCode = (int)status;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            sockets.Enqueue(socket);
            var buffer = new byte[16 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    received.Enqueue(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        });
        app.StartAsync().GetAwaiter().GetResult();
        Url = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("http://", "ws://"));
    }

    public sealed record Connected(string Path, string Query, IReadOnlyDictionary<string, string> Headers);

    public HttpStatusCode? RefuseWith { get; set; }

    public Uri Url { get; }

    public ConcurrentQueue<Connected> Requests { get; } = new();

    public IReadOnlyList<string> Received => [.. received];

    public int Connections => Requests.Count;

    /// <summary>The address a plugin is pointed at (no trailing slash).</summary>
    public string Address => Url.ToString().TrimEnd('/');

    public async Task SendBinaryAsync(byte[] frame) => await Current().SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

    public async Task SendTextAsync(string text) => await Current().SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Drops the newest connection without a closing handshake, as a network failure does.</summary>
    public void Drop() => Current().Abort();

    private WebSocket Current()
    {
        var all = sockets.ToArray();
        return all.Length > 0 ? all[^1] : throw new InvalidOperationException("Nothing has connected to the fake broker yet.");
    }

    public static async Task<bool> UntilAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in sockets) s.Abort();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
