using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Mcp;

/// <summary>MCP over a pipe: one JSON message per line in, one per line out. Nothing but protocol may be written to the output.</summary>
public sealed class StdioTransport(McpServer server, McpSession session)
{
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct);
            if (line is null) return; // the client closed the pipe
            if (string.IsNullOrWhiteSpace(line)) continue;

            McpOutcome outcome;
            try
            {
                outcome = await server.HandleAsync(JsonNode.Parse(line), session, ct);
            }
            catch (JsonException)
            {
                outcome = new McpOutcome(McpServer.ParseFailure());
            }

            if (outcome.Response is null) continue;
            // Compact and on one line, so a client reading by line gets exactly one message.
            await output.WriteLineAsync(outcome.Response.ToJsonString());
            await output.FlushAsync(ct);
        }
    }
}
