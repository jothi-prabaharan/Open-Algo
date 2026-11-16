using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Motilal;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>Motilal depth and several quotes against the Python client (tools/contract-fixtures/feed_snap_motilal.fixtures.json): the real plugin with a connection that hands back the frames the recording scripted, one by one.</summary>
public sealed class MotilalSnapshotContractTests
{
    private const string File = "feed_snap_motilal.fixtures.json";

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

    /// <summary>The Python client's connection: the first frame signs it in, then each frame is handed over in turn until the answer is found or they run out.</summary>
    private sealed class Script(JsonObject step) : IMotilalSockets
    {
        private readonly Queue<byte[]> frames = new((step["frames"] as JsonArray ?? []).OfType<JsonObject>().Select(f => Convert.FromHexString(f["hex"]!.GetValue<string>())));

        public List<JsonNode?> Sent { get; } = [];

        private static JsonNode Of(MotilalMessage m) => m.Binary ? new JsonObject { ["binary"] = Convert.ToHexStringLower(m.Bytes) } : new JsonObject { ["text"] = m.Text };

        public Task<bool> RunAsync(Uri address, byte[] login, IReadOnlyList<MotilalMessage> subscribes, IReadOnlyList<MotilalMessage> unsubscribes, Func<byte[], bool> onFrame, TimeSpan wait, CancellationToken cancellationToken)
        {
            if (step["connect"] is JsonValue connect && !connect.GetValue<bool>()) return Task.FromResult(false);
            Sent.Add(Of(MotilalMessage.Of(login)));
            if (frames.Count == 0) return Task.FromResult(false);
            onFrame(frames.Dequeue());
            foreach (var message in subscribes) Sent.Add(Of(message));
            while (frames.Count > 0 && wait > TimeSpan.Zero)
                if (onFrame(frames.Dequeue())) break;
            foreach (var message in unsubscribes) Sent.Add(Of(message));
            return Task.FromResult(true);
        }
    }

    private static string Reason(string python) => python switch
    {
        var p when p.Contains("could not establish WebSocket connection", StringComparison.Ordinal) || p.Contains("WebSocket connection unavailable", StringComparison.Ordinal) => "did not answer on its live price connection",
        var p when p.Contains("index streaming is not supported", StringComparison.Ordinal) => "gives no depth for",
        var p when p.Contains("no index data received", StringComparison.Ordinal) => "sent no value for",
        _ => python,
    };

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Matches_the_python_client(string name)
    {
        var fixtures = BrokerContractHost.Load(File);
        var rows = fixtures["symbols"]!.AsArray().OfType<JsonObject>().Select(s => new SymbolRecord(s["symbol"]!.GetValue<string>(), s["brsymbol"]!.GetValue<string>(), s["exchange"]!.GetValue<string>(), s["brexchange"]!.GetValue<string>(),
            s["token"]!.GetValue<string>(), s["name"]?.GetValue<string>(), null, null, s["lotsize"]!.GetValue<int>(), s["instrumenttype"]?.GetValue<string>(), 0.05)).ToList();
        var session = new BrokerSession(Guid.NewGuid(), "motilal", fixtures["access_token"]!.GetValue<string>(), null, fixtures["client_id"]!.GetValue<string>(), new Dictionary<string, string>());
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
            var plugin = new MotilalPlugin(null!, sessions, new Master(rows), TimeProvider.System, sockets);
            JsonNode? actual = null;
            string? error = null;
            try
            {
                actual = kind switch
                {
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
                var diffs = JsonCompare.Differences(result["result"]!.DeepClone(), actual);
                Assert.True(diffs.Count == 0, $"{label}: the answer differs:\n{string.Join("\n", diffs)}");
            }

            var expectedSent = result["sent"]!.AsArray();
            Assert.True(expectedSent.Count == sockets.Sent.Count, $"{label}: expected {expectedSent.Count} messages, got {sockets.Sent.Count}");
            for (var i = 0; i < expectedSent.Count; i++)
            {
                var diffs = JsonCompare.Differences(expectedSent[i], sockets.Sent[i]);
                Assert.True(diffs.Count == 0, $"{label}: message {i} differs:\n{string.Join("\n", diffs)}");
            }
        }
    }
}

/// <summary>Motilal's stream against the Python adapter (tools/contract-fixtures/feed_motilal.fixtures.json).</summary>
public sealed class MotilalFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_motilal.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_motilal.fixtures.json", name, () => new MotilalFeedCodec());
}
