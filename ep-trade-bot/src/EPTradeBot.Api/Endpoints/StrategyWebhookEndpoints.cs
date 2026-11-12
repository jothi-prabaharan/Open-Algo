using EPTradeBot.Infrastructure.OpenAlgoApi;

namespace EPTradeBot.Api.Endpoints;

/// <summary>
/// POST /api/v1/strategy/webhook/{token}: the public route an alert (TradingView, Chartink) calls to start or stop a batch strategy. It carries no API key because the sender cannot set a header; the token in the
/// address is the credential and the service checks it, the lock, the address allowlist and the body, in that order. It always answers with its own body, never the framework's 404, so a scanner
/// walking tokens is told nothing and does not count towards an address ban.
/// </summary>
public static class StrategyWebhookEndpoints
{
    public static void MapStrategyWebhook(this RouteGroupBuilder v1)
    {
        v1.MapPost("/strategy/webhook/{token?}", async (HttpContext c, string? token) =>
        {
            var service = c.RequestServices.GetRequiredService<StrategyWebhookService>();
            using var buffer = new MemoryStream();
            // One byte over the cap is enough to know it is too large, so a huge body is never held whole.
            await c.Request.Body.CopyToAsync(new LimitedWriter(buffer, StrategyWebhookService.MaxPayloadBytes + 1), c.RequestAborted);
            var outcome = await service.HandleAsync(token, buffer.ToArray(), c.Connection.RemoteIpAddress?.ToString(), c.Request.Headers.UserAgent.ToString(), c.RequestAborted);
            c.Response.StatusCode = outcome.Status;
            await c.Response.WriteAsJsonAsync(outcome.Body(), c.RequestAborted);
        });
    }

    private sealed class LimitedWriter(Stream inner, int limit) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = limit - (int)inner.Length;
            if (room > 0) inner.Write(buffer, offset, Math.Min(count, room));
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var room = limit - (int)inner.Length;
            if (room > 0) inner.Write(buffer.Span[..Math.Min(buffer.Length, room)]);
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
