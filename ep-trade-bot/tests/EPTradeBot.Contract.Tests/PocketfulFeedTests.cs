using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Pocketful;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>Pocketful quotes, depth and several quotes against the Python client (tools/contract-fixtures/feed_snap_pocketful.fixtures.json): the real plugin with a connection that hands back the frames the recording scripted, one by one.</summary>
public sealed class PocketfulSnapshotContractTests
{
    private const string File = "feed_snap_pocketful.fixtures.json";

    public static IEnumerable<object[]> Names() => FeedRunner.Names(File);

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master(IReadOnlyList<SymbolRecord> rows) : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult(rows.FirstOrDefault(r => r.Symbol == symbol && r.Exchange == exchange));

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }

    /// <summary>The Python client's connection: the frames of the scenario, one for each time the client looked, until the answer is found or they run out.</summary>
    private sealed class Script(JsonObject step) : IPocketfulSockets
    {
        public List<JsonNode?> Sent { get; } = [];

        public List<string> Connections { get; } = [];

        public Task<bool> RunAsync(Uri address, IReadOnlyList<string> subscribes, IReadOnlyList<string> unsubscribes, Func<PocketfulFrame, bool> onFrame, TimeSpan wait, CancellationToken cancellationToken)
        {
            Connections.Add(address.OriginalString);
            if (step["connect"] is JsonValue connect && !connect.GetValue<bool>()) return Task.FromResult(false);
            foreach (var text in subscribes) Sent.Add(JsonNode.Parse(text));
            foreach (var item in (step["frames"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var frame = item["hex"] is JsonValue hex ? PocketfulFrame.Of(Convert.FromHexString(hex.GetValue<string>())) : PocketfulFrame.Of(item["text"]!.GetValue<string>());
                if (onFrame(frame)) break;
            }

            foreach (var text in unsubscribes) Sent.Add(JsonNode.Parse(text));
            return Task.FromResult(true);
        }
    }

    private static string Reason(string python) => python switch
    {
        var p when p.Contains("Failed to establish WebSocket connection", StringComparison.Ordinal) => "did not open its market data connection",
        var p when p.Contains("No detailed market data received", StringComparison.Ordinal) => "did not give a quote for",
        var p when p.Contains("No market depth data received", StringComparison.Ordinal) => "did not give the market depth for",
        _ => python,
    };

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Matches_the_python_client(string name)
    {
        var fixtures = BrokerContractHost.Load(File);
        var rows = fixtures["symbols"]!.AsArray().OfType<JsonObject>().Select(s => new SymbolRecord(s["symbol"]!.GetValue<string>(), s["brsymbol"]!.GetValue<string>(), s["exchange"]!.GetValue<string>(), s["brexchange"]!.GetValue<string>(),
            s["token"]!.GetValue<string>(), s["name"]?.GetValue<string>(), null, null, 1, s["instrumenttype"]?.GetValue<string>(), 0.05)).ToList();
        var session = new BrokerSession(Guid.NewGuid(), "pocketful", fixtures["access_token"]!.GetValue<string>(), null, fixtures["client_id"]!.GetValue<string>(), new Dictionary<string, string>());
        var sessions = new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(new EPTradeBot.Core.Configuration.BrokerOptions()));
        var scenario = fixtures["scenarios"]!.AsArray().OfType<JsonObject>().Single(s => s["name"]!.GetValue<string>() == name);
        var index = 0;
        foreach (var entry in scenario["steps"]!.AsArray().OfType<JsonObject>())
        {
            var step = entry["step"]!.AsObject();
            var kind = step.First().Key;
            var inner = step[kind]!.AsObject();
            var result = entry["result"]!.AsObject();
            var label = $"{name} step {++index}";
            var sockets = new Script(inner);
            var plugin = new PocketfulPlugin(null!, sessions, new Master(rows), TimeProvider.System, sockets);
            JsonNode? actual = null;
            string? error = null;
            try
            {
                actual = kind switch
                {
                    "quote" => await plugin.GetQuoteAsync(session, inner["symbol"]!.GetValue<string>(), inner["exchange"]!.GetValue<string>(), CancellationToken.None),
                    "depth" => await plugin.GetDepthAsync(session, inner["symbol"]!.GetValue<string>(), inner["exchange"]!.GetValue<string>(), CancellationToken.None),
                    _ => await plugin.GetMultiQuotesAsync(session, inner["symbols"]!.AsArray().OfType<JsonObject>().Select(s => rows.First(r => r.Symbol == s["symbol"]!.GetValue<string>() && r.Exchange == s["exchange"]!.GetValue<string>())).ToList(), CancellationToken.None),
                };
            }
            catch (BrokerException ex)
            {
                error = ex.Message;
            }

            if (result["error"] is JsonValue expectedError)
            {
                Assert.True(error is not null, $"{label}: expected an error ({expectedError.GetValue<string>()}), got an answer {actual?.ToJsonString()}");
                Assert.Contains(Reason(expectedError.GetValue<string>()), error!, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(error is null, $"{label}: unexpected error: {error}");
                var expected = result["result"]!.DeepClone();
                // A connection that would not open is told per symbol in OpenAlgo's words; EP Trade bot's words say what to do.
                if (expected is JsonArray list)
                    foreach (var item in list.OfType<JsonObject>().Where(i => i["error"] is JsonValue e && e.GetValue<string>().StartsWith("WebSocket connection failed", StringComparison.Ordinal)))
                        item["error"] = "Pocketful did not open its market data connection. Try again in a moment.";
                var diffs = JsonCompare.Differences(expected, actual);
                Assert.True(diffs.Count == 0, $"{label}: the answer differs:\n{string.Join("\n", diffs)}");
            }

            var expectedSent = result["sent"]!.AsArray();
            Assert.True(expectedSent.Count == sockets.Sent.Count, $"{label}: expected {expectedSent.Count} messages, got {sockets.Sent.Count}");
            for (var i = 0; i < expectedSent.Count; i++)
            {
                var diffs = JsonCompare.Differences(expectedSent[i], sockets.Sent[i]);
                Assert.True(diffs.Count == 0, $"{label}: message {i} differs:\n{string.Join("\n", diffs)}");
            }

            Assert.Equal(result["connections"]!.AsArray().Count, sockets.Connections.Count);
            foreach (var (expected, got) in result["connections"]!.AsArray().Zip(sockets.Connections))
                Assert.Equal(expected!.GetValue<string>(), got);
        }
    }
}

/// <summary>Pocketful's stream against the Python adapter (tools/contract-fixtures/feed_pocketful.fixtures.json).</summary>
public sealed class PocketfulFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_pocketful.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_pocketful.fixtures.json", name, () => new PocketfulFeedCodec());
}
