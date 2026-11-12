using EPTradeBot.Core.Mcp;

// The stdio MCP server: an MCP client (Claude Desktop, Cursor, ...) starts this program and talks to it over its input and output. It reaches EP Trade bot over HTTP with an
// API key, so a semi-auto key's orders wait in the Action Center. Standard output carries protocol only; anything for a person goes to standard error.
//
//   EPTRADE_API_KEY          required   an API key made on the Accounts page
//   EPTRADE_BASE_URL         optional   where EP Trade bot is, default http://127.0.0.1:5000
//   EPTRADE_MCP_READ_ONLY    optional   1 to offer only the tools that read
//   EPTRADE_MCP_TOOLSETS     optional   comma list from orders, account, marketdata, research, system

var key = Environment.GetEnvironmentVariable("EPTRADE_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    await Console.Error.WriteLineAsync("EPTRADE_API_KEY is not set. Make an API key for your account in EP Trade bot and put it in this variable.");
    return 2;
}

var baseUrl = Environment.GetEnvironmentVariable("EPTRADE_BASE_URL") is { Length: > 0 } b ? b : "http://127.0.0.1:5000";
if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
{
    await Console.Error.WriteLineAsync($"EPTRADE_BASE_URL '{baseUrl}' is not an address starting with http:// or https://.");
    return 2;
}

var readOnly = Environment.GetEnvironmentVariable("EPTRADE_MCP_READ_ONLY") is "1" or "true" or "TRUE";
var toolsets = Environment.GetEnvironmentVariable("EPTRADE_MCP_TOOLSETS") is { Length: > 0 } t
    ? t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
    : null;

using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(60) };
var server = new McpServer(McpTools.All, new McpPolicy { ReadOnly = readOnly, Toolsets = toolsets }, new McpRateLimiter(), audit: null);
var session = new McpSession { Api = new HttpPlatformApi(http, key), ClientId = "stdio", TokenId = "stdio" };
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

try
{
    await new StdioTransport(server, session).RunAsync(Console.In, Console.Out, stop.Token);
}
catch (OperationCanceledException)
{
}

return 0;
