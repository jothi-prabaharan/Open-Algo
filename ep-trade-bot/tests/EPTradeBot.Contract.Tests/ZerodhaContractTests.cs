using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Brokers;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>
/// One EP Trade bot host with a LIVE Zerodha account, shared by every Zerodha fixture.
/// Production environment, so the guard lets LIVE orders through to the (fake) broker.
/// </summary>
public sealed class ZerodhaHost : IAsyncLifetime
{
    public FakeBroker Broker { get; } = new();
    public TestHost? Host { get; private set; }
    public string ApiKey { get; private set; } = "";
    public JsonObject Fixtures { get; } = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zerodha.fixtures.json")))!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable))) return;
        Host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "zerodha");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => Broker)));
        });

        var accountId = await Host.CreateAccountAsync(AccountType.LIVE, "KiteMain", maxLoss: 1_000_000_000m, maxQty: 100_000, maxValue: 100_000_000_000m);
        (await Host.Admin.PutAsJsonAsync($"/api/v1/accounts/{accountId}/broker-session", new { auth_token = Fixtures["auth_token"]!.GetValue<string>() })).EnsureSuccessStatusCode();
        ApiKey = (await Host.Get<ApiKeyService>().IssueAsync(accountId, "contract tests")).Key;

        await using var db = Host.NewDb();
        foreach (var row in Fixtures["symbols"]!.AsArray().OfType<JsonObject>())
        {
            db.Set<SymToken>().Add(new SymToken
            {
                Broker = "zerodha", Symbol = Text(row, "symbol")!, Brsymbol = Text(row, "brsymbol")!, Name = Text(row, "name"),
                Exchange = Text(row, "exchange"), Brexchange = Text(row, "brexchange"), Token = Text(row, "token"), Expiry = Text(row, "expiry"),
                Strike = row["strike"]?.GetValue<double>(), Lotsize = row["lotsize"]?.GetValue<int>(), Instrumenttype = Text(row, "instrumenttype"),
                TickSize = row["tick_size"]?.GetValue<double>(),
            });
        }

        await db.SaveChangesAsync();

        // The market feed supplies prices to the risk check in production; here they are set directly.
        Host.SetQuote("NSE", "SBIN", 805m);
        Host.SetQuote("NSE", "INFY", 1500m);
        Host.SetQuote("NFO", "NIFTY25NOV26FUT", 24000m);
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// Replays every request recorded from the Python implementation against EP Trade bot,
/// with the same Zerodha replies, and requires the same answer and the same writes to
/// Zerodha (orders placed, changed, cancelled), field for field.
/// </summary>
public sealed class ZerodhaContractTests(ZerodhaHost fixture, ITestOutputHelper output) : IClassFixture<ZerodhaHost>
{
    /// <summary>
    /// Where EP Trade bot deliberately answers differently from OpenAlgo, the answer it gives
    /// instead and why. Every other fixture must match.
    /// </summary>
    public static readonly Dictionary<string, (int Status, string Reason)> Deviations = new()
    {
        ["placeorder_unknown_symbol"] = (400,
            "OpenAlgo sends tradingsymbol \"None\" to Kite for a symbol missing from the master contract. EP Trade bot refuses before sending anything."),
        ["auth_header_key"] = (200,
            "OpenAlgo's /funds schema requires apikey in the body, so the X-API-KEY header is refused there. EP Trade bot accepts the header on every endpoint."),
    };

    /// <summary>Reads EP Trade bot may make that the Python request did not (the risk check reads positions before every order).</summary>
    private static readonly Dictionary<string, string> HarnessReplies = new()
    {
        ["GET /portfolio/positions"] = """{"status":"success","data":{"net":[],"day":[]}}""",
    };

    public static IEnumerable<object[]> Names()
    {
        var fixtures = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zerodha.fixtures.json")))!;
        return fixtures["fixtures"]!.AsArray().Select(f => new object[] { f!["name"]!.GetValue<string>() });
    }

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public async Task Matches_the_python_implementation(string name)
    {
        var f = fixture.Fixtures["fixtures"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == name);
        var recordedKey = fixture.Fixtures["api_key"]!.GetValue<string>();

        var replies = new Dictionary<string, (int, string)>();
        foreach (var (key, value) in f["broker"]!.AsObject())
            replies[key] = (value!["status"]?.GetValue<int>() ?? 200, value["body"]!.ToJsonString());
        foreach (var (key, body) in HarnessReplies) replies.TryAdd(key, (200, body));
        fixture.Broker.Reset(replies);

        var request = (JsonObject)f["request"]!.DeepClone();
        if (request["apikey"] is JsonValue k && k.GetValue<string>() == recordedKey) request["apikey"] = fixture.ApiKey;
        using var client = fixture.Host!.Anonymous();
        using var message = new HttpRequestMessage(HttpMethod.Post, f["path"]!.GetValue<string>())
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (f["auth"]!.GetValue<string>() == "header") message.Headers.Add("X-API-KEY", fixture.ApiKey);
        using var response = await client.SendAsync(message);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var expected = f["expected"]!;
        output.WriteLine($"EP Trade bot answered {(int)response.StatusCode}: {actual?.ToJsonString()}");

        if (Deviations.TryGetValue(name, out var deviation))
        {
            output.WriteLine($"Deliberate difference: {deviation.Reason}");
            Assert.Equal(deviation.Status, (int)response.StatusCode);
            if (deviation.Status != 200) Assert.Empty(Writes(fixture.Broker.Sent));
            return;
        }

        Assert.Equal(expected["status"]!.GetValue<int>(), (int)response.StatusCode);
        var expectedBody = expected["body"];
        if (IsSchemaMessage(expected))
        {
            // Validation wording comes from marshmallow; the contract is the status and that the field is named.
            Assert.Equal("error", actual?["status"]?.GetValue<string>());
            Assert.Empty(fixture.Broker.Sent);
            return;
        }

        var diffs = JsonCompare.Differences(expectedBody, actual);
        Assert.True(diffs.Count == 0, "Response differs from OpenAlgo:\n" + string.Join("\n", diffs));
        CompareBrokerTraffic(f, fixture.Broker.Sent, replies.Keys);
    }

    private static bool IsSchemaMessage(JsonNode expected) =>
        expected["status"]!.GetValue<int>() == 400 && expected["body"]?["message"] is { } m
        && (m is JsonObject || (m is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('{')));

    private static List<SentRequest> Writes(IEnumerable<SentRequest> sent) => sent.Where(s => s.Method != "GET").ToList();

    private void CompareBrokerTraffic(JsonObject f, IReadOnlyList<SentRequest> actualSent, IEnumerable<string> answered)
    {
        var expectedSent = f["sent"]!.AsArray().OfType<JsonObject>().ToList();

        // Writes: the same orders, changes and cancels, in the same order, field for field.
        var expectedWrites = expectedSent.Where(s => s["method"]!.GetValue<string>() != "GET").ToList();
        var actualWrites = Writes(actualSent);
        Assert.True(expectedWrites.Count == actualWrites.Count,
            $"Expected {expectedWrites.Count} writes to the broker, EP Trade bot sent {actualWrites.Count}: {string.Join(", ", actualWrites.Select(w => $"{w.Method} {w.Path}"))}");
        for (var i = 0; i < expectedWrites.Count; i++)
        {
            var e = expectedWrites[i];
            var a = actualWrites[i];
            Assert.Equal(e["method"]!.GetValue<string>(), a.Method);
            Assert.Equal(e["path"]!.GetValue<string>(), a.Path);
            CompareHeaders(e, a);
            if (e["form"] is JsonObject form)
            {
                Assert.NotNull(a.Form);
                var actualForm = a.Form!.ToDictionary(kv => kv.Key, kv => kv.Value);
                Assert.Equal(form.Select(kv => kv.Key).OrderBy(x => x), actualForm.Keys.OrderBy(x => x));
                foreach (var (key, value) in form)
                {
                    if (key == "tag")
                    {
                        // A fresh tag per order, used to find an order whose reply was lost.
                        Assert.Matches("^openalgo[0-9a-f]{12}$", actualForm[key]);
                        continue;
                    }

                    var ev = value!.GetValue<string>();
                    Assert.True(JsonCompare.SameValue(ev, actualForm[key]), $"Write {i} field {key}: expected \"{ev}\", got \"{actualForm[key]}\"");
                }
            }
        }

        // Reads: every read the Python plugin made is made, with the same query; any extra read must be one with a reply.
        var answeredSet = answered.ToHashSet();
        foreach (var e in expectedSent.Where(s => s["method"]!.GetValue<string>() == "GET"))
        {
            var path = e["path"]!.GetValue<string>();
            var query = e["query"]!.AsArray().Select(p => $"{p![0]}={p[1]}").ToList();
            Assert.True(actualSent.Any(a => a.Method == "GET" && a.Path == path && a.Query.Select(q => $"{q.Key}={q.Value}").SequenceEqual(query)),
                $"Expected a read of {path}?{string.Join("&", query)}; EP Trade bot read: {string.Join(", ", actualSent.Where(s => s.Method == "GET").Select(s => s.Path + "?" + string.Join("&", s.Query.Select(q => $"{q.Key}={q.Value}"))))}");
        }

        foreach (var a in actualSent.Where(s => s.Method == "GET"))
            Assert.True(answeredSet.Contains($"GET {a.Path}"), $"EP Trade bot read {a.Path}, which the Python implementation never reads here.");
    }

    private static void CompareHeaders(JsonObject expected, SentRequest actual)
    {
        foreach (var (key, value) in expected["headers"]!.AsObject())
        {
            var ev = value!.GetValue<string>();
            Assert.True(actual.Headers.TryGetValue(key, out var av), $"Header {key} missing");
            if (key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) Assert.StartsWith(ev, av, StringComparison.OrdinalIgnoreCase);
            else Assert.Equal(ev, av);
        }
    }
}
