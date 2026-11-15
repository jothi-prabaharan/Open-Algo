using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Infrastructure.Brokers.Nubra;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>Nubra quotes, depth and several quotes against the Python client (tools/contract-fixtures/feed_snap_nubra.fixtures.json): the real plugin with a connection that hands back the frames the recording scripted, one for each tick, and a scripted REST client.</summary>
public sealed class NubraSnapshotContractTests
{
    private const string File = "feed_snap_nubra.fixtures.json";
    private const string Token = "nubra-session-token";

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

    /// <summary>The feed: the frames of the step, one for each tick, whatever the client has asked.</summary>
    private sealed class Script(JsonObject step) : INubraSockets
    {
        private readonly Queue<byte[]> frames = new((step["ws"]?["frames"] as JsonArray ?? []).OfType<JsonObject>().Select(f => Convert.FromHexString(f["hex"]!.GetValue<string>())));
        private readonly bool connect = step["ws"]?["connect"] is not JsonValue c || c.GetValue<bool>();

        public List<string> Sent { get; } = [];

        public int Opened { get; private set; }

        public Task<INubraConnection?> OpenAsync(Uri address, string token, string deviceId, CancellationToken cancellationToken)
        {
            Assert.Equal("wss://api.nubra.io/apibatch/ws", address.OriginalString);
            Assert.Equal(Token, token);
            Assert.Equal("OPENALGO", deviceId);
            if (!connect) return Task.FromResult<INubraConnection?>(null);
            Opened++;
            return Task.FromResult<INubraConnection?>(new Link(this));
        }

        private sealed class Link(Script owner) : INubraConnection
        {
            public Task<bool> SendAsync(string text, CancellationToken cancellationToken)
            {
                owner.Sent.Add(text.Replace(Token, "<token>", StringComparison.Ordinal));
                return Task.FromResult(true);
            }

            public Task<IReadOnlyList<byte[]>> TickAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<byte[]>>(owner.frames.Count > 0 ? [owner.frames.Dequeue()] : []);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Rest(JsonArray replies) : IHttpClientFactory
    {
        private readonly Queue<JsonObject> queue = new(replies.OfType<JsonObject>());

        public List<JsonObject> Calls { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Rest owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner)
                {
                    owner.Calls.Add(new JsonObject { ["method"] = request.Method.Method, ["url"] = request.RequestUri!.PathAndQuery });
                    var spec = owner.queue.Count > 0 ? owner.queue.Dequeue() : null;
                    var status = spec?["status"]?.GetValue<int>() ?? 599;
                    var body = spec?["body"];
                    var text = body is JsonValue v && v.TryGetValue<string>(out var raw) ? raw : body?.ToJsonString() ?? "";
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
                }
            }
        }
    }

    private static string Reason(string python) => python switch
    {
        var p when p.Contains("Authentication failed", StringComparison.Ordinal) => "did not accept the session token",
        _ => python,
    };

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Matches_the_python_client(string name)
    {
        var fixtures = BrokerContractHost.Load(File);
        var rows = fixtures["symbols"]!.AsArray().OfType<JsonObject>().Select(s => new SymbolRecord(s["symbol"]!.GetValue<string>(), s["brsymbol"]!.GetValue<string>(), s["exchange"]!.GetValue<string>(), s["brexchange"]!.GetValue<string>(),
            s["token"]!.GetValue<string>(), s["name"]?.GetValue<string>(), null, null, 1, s["instrumenttype"]?.GetValue<string>(), 0.05)).ToList();
        var session = new BrokerSession(Guid.NewGuid(), "nubra", fixtures["access_token"]!.GetValue<string>(), null, null, new Dictionary<string, string>());
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.RetryBackoffMs["nubra"] = 0;
        var sessions = new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options));
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
            var rest = new Rest(inner["rest"] as JsonArray ?? []);
            var plugin = new NubraPlugin(rest, sessions, new Master(rows), TimeProvider.System, sockets);
            JsonNode? actual = null;
            string? error = null;
            try
            {
                actual = kind switch
                {
                    "quote" => await plugin.GetQuoteAsync(session, inner["symbol"]!.GetValue<string>(), inner["exchange"]!.GetValue<string>(), CancellationToken.None),
                    "depth" => await plugin.GetDepthAsync(session, inner["symbol"]!.GetValue<string>(), inner["exchange"]!.GetValue<string>(), CancellationToken.None),
                    _ => await plugin.GetMultiQuotesAsync(session, inner["symbols"]!.AsArray().OfType<JsonArray>().Select(s => rows.First(r => r.Symbol == s[0]!.GetValue<string>() && r.Exchange == s[1]!.GetValue<string>())).ToList(), CancellationToken.None),
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
                if (actual is JsonArray list) actual = new JsonArray(list.OfType<JsonObject>().OrderBy(r => r["exchange"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(r => r["symbol"]!.GetValue<string>(), StringComparer.Ordinal).Select(r => (JsonNode?)r.DeepClone()).ToArray());
                var diffs = JsonCompare.Differences(expected, actual);
                Assert.True(diffs.Count == 0, $"{label}: the answer differs:\n{string.Join("\n", diffs)}");
            }

            var expectedSent = result["sent"]!.AsArray();
            Assert.True(expectedSent.Count == sockets.Sent.Count, $"{label}: expected {expectedSent.Count} messages, got {sockets.Sent.Count}:\n{string.Join("\n", sockets.Sent)}");
            for (var i = 0; i < expectedSent.Count; i++) Assert.Equal(expectedSent[i]!.GetValue<string>(), sockets.Sent[i]);

            var expectedHttp = result["http"]!.AsArray();
            Assert.True(expectedHttp.Count == rest.Calls.Count, $"{label}: expected {expectedHttp.Count} REST calls, got {rest.Calls.Count}: {string.Join(", ", rest.Calls.Select(c => c.ToJsonString()))}");
            for (var i = 0; i < expectedHttp.Count; i++)
            {
                var diffs = JsonCompare.Differences(expectedHttp[i], rest.Calls[i]);
                Assert.True(diffs.Count == 0, $"{label}: REST call {i} differs:\n{string.Join("\n", diffs)}");
            }
        }
    }
}
