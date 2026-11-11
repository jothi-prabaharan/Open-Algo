using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.IntegrationTests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace EPTradeBot.Contract.Tests;

/// <summary>
/// A deliberate difference from OpenAlgo. With <see cref="Adjust"/>, the fixture is changed to
/// the answer EP Trade bot gives instead and then compared in full (body and broker traffic).
/// With only <see cref="Status"/>, the status is checked and nothing may be written to the broker.
/// </summary>
public sealed record Deviation(string Reason, Action<JsonObject>? Adjust = null, int? Status = null);

/// <summary>
/// One EP Trade bot host with a LIVE account on one broker, shared by that broker's fixtures.
/// Production environment, so the guard lets LIVE orders through to the (fake) broker.
/// </summary>
public abstract class BrokerContractHost : IAsyncLifetime
{
    public FakeBroker Broker { get; } = new();
    public TestHost? Host { get; private set; }
    public string ApiKey { get; private set; } = "";
    public Guid AccountId { get; private set; }

    /// <summary>A PAPER account on the same broker, for fixtures recorded in analyze mode (<c>"analyze": true</c>).</summary>
    public string PaperApiKey { get; private set; } = "";
    public Guid PaperAccountId { get; private set; }
    public JsonObject Fixtures { get; }

    /// <summary>Whether the fixtures include analyze-mode ones, which run on a PAPER account.</summary>
    public virtual bool WithSandboxAccount => false;

    /// <summary>Stored state a fixture needs before its request (saved preferences, sandbox positions).</summary>
    public virtual Task PrepareAsync(JsonObject fixture) => Task.CompletedTask;

    protected BrokerContractHost() => Fixtures = Load(FixtureFile);

    public abstract string BrokerName { get; }
    public abstract string FixtureFile { get; }

    /// <summary>Prices the market feed would supply to the risk check.</summary>
    public abstract IReadOnlyDictionary<(string Exchange, string Symbol), decimal> Prices { get; }

    /// <summary>Replies for reads EP Trade bot makes that the Python request did not (the risk check reads positions before each order).</summary>
    public abstract IReadOnlyDictionary<string, string> HarnessReplies { get; }

    /// <summary>Fields that differ on every request by design (a fresh order tag, today's date), as "form.tag" or "json.fromDate", with the format each must have.</summary>
    public virtual IReadOnlyDictionary<string, string> VolatileFields { get; } = new Dictionary<string, string>();

    /// <summary>The broker's application keys (Brokers:Keys:&lt;broker&gt;), as the recorded install had them in its environment.</summary>
    public virtual IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string>();

    /// <summary>Further configuration for the host (a spacing between calls that a fixture run does not wait for).</summary>
    public virtual IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();

    /// <summary>Paths of reads the Python plugin makes only to log something (Delta's margin mode), which EP Trade bot does not make.</summary>
    public virtual IReadOnlySet<string> IgnoredReads { get; } = new HashSet<string>();

    /// <summary>A check of every request the broker would see (a signature, say), beyond the comparison with the recording.</summary>
    public virtual void Verify(SentRequest request)
    {
    }

    /// <summary>The instant the fixtures were recorded, for a broker whose answers depend on today's date.</summary>
    public DateTimeOffset? RecordedAt => Fixtures["recorded_at"] is JsonValue v && v.TryGetValue<string>(out var s) ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The broker takes a number and its text form alike, and OpenAlgo itself sends both, so request bodies compare by value.</summary>
    public virtual bool NumbersMayBeText => false;

    public static JsonObject Load(string file) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)))!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable))) return;
        Host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", BrokerName);
            foreach (var (key, value) in Keys) b.UseSetting($"Brokers:Keys:{BrokerName}:{key}", value);
            foreach (var (key, value) in Settings) b.UseSetting(key, value);
            b.ConfigureServices(s =>
            {
                s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => Broker));
                if (RecordedAt is { } at) s.AddSingleton<TimeProvider>(new FixedClock(at));
            });
        });

        var clientId = Fixtures["client_id"]?.GetValue<string>() ?? "contract";
        var accountId = await Host.CreateAccountAsync(AccountType.LIVE, "Contract", maxLoss: 1_000_000_000m, maxQty: 100_000, maxValue: 100_000_000_000m,
            broker: BrokerName, clientId: clientId);
        (await Host.Admin.PutAsJsonAsync($"/api/v1/accounts/{accountId}/broker-session", new { auth_token = Fixtures["auth_token"]!.GetValue<string>() })).EnsureSuccessStatusCode();
        ApiKey = (await Host.Get<ApiKeyService>().IssueAsync(accountId, "contract tests")).Key;
        AccountId = accountId;
        if (WithSandboxAccount)
        {
            PaperAccountId = await Host.CreateAccountAsync(AccountType.PAPER, "Contract sandbox", broker: BrokerName, clientId: clientId + "-sandbox");
            PaperApiKey = (await Host.Get<ApiKeyService>().IssueAsync(PaperAccountId, "contract tests")).Key;
        }

        await using var db = Host.NewDb();
        foreach (var row in Fixtures["symbols"]!.AsArray().OfType<JsonObject>())
        {
            db.Set<SymToken>().Add(new SymToken
            {
                Broker = BrokerName, Symbol = Text(row, "symbol")!, Brsymbol = Text(row, "brsymbol")!, Name = Text(row, "name"),
                Exchange = Text(row, "exchange"), Brexchange = Text(row, "brexchange"), Token = Text(row, "token"), Expiry = Text(row, "expiry"),
                Strike = row["strike"]?.GetValue<double>(), Lotsize = row["lotsize"]?.GetValue<int>(), Instrumenttype = Text(row, "instrumenttype"),
                TickSize = row["tick_size"]?.GetValue<double>(), ContractValue = row["contract_value"]?.GetValue<double>(),
            });
        }

        // The recorded install's freeze limits, over the ones seeded at startup.
        foreach (var row in (Fixtures["freeze"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var (exchange, symbol, quantity) = (Text(row, "exchange")!, Text(row, "symbol")!, row["freeze_qty"]!.GetValue<int>());
            var existing = db.Set<QtyFreeze>().SingleOrDefault(q => q.Exchange == exchange && q.Symbol == symbol);
            if (existing is null) db.Set<QtyFreeze>().Add(new QtyFreeze { Exchange = exchange, Symbol = symbol, FreezeQty = quantity });
            else existing.FreezeQty = quantity;
        }

        await db.SaveChangesAsync();
        foreach (var ((exchange, symbol), price) in Prices) Host.SetQuote(exchange, symbol, price);
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>A clock stopped at one instant.</summary>
public sealed class FixedClock(DateTimeOffset at) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => at;
}

/// <summary>
/// Replays a request recorded from the Python implementation against EP Trade bot, with the
/// same broker replies, and requires the same answer and the same writes to the broker, field
/// for field. Reads (GETs, and POSTs the broker uses for reading) must include every read the
/// Python plugin made, with the same query and body; an extra read must be one with a reply.
/// </summary>
public static class ContractRunner
{
    public static IEnumerable<object[]> Names(string file) =>
        BrokerContractHost.Load(file)["fixtures"]!.AsArray().Select(f => new object[] { f!["name"]!.GetValue<string>() });

    public static async Task RunAsync(BrokerContractHost host, string name, IReadOnlyDictionary<string, Deviation> deviations, ITestOutputHelper output)
    {
        var f = (JsonObject)host.Fixtures["fixtures"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == name).DeepClone();
        var recordedKey = host.Fixtures["api_key"]!.GetValue<string>();
        var readPosts = (host.Fixtures["read_posts"] as JsonArray ?? []).Select(p => p!.GetValue<string>()).ToHashSet();

        var replies = new Dictionary<string, List<(int, string)>>();
        foreach (var (key, value) in f["broker"]!.AsObject())
        {
            replies[key] = value is JsonArray list
                ? list.Select(r => (r!["status"]?.GetValue<int>() ?? 200, r["body"]!.ToJsonString())).ToList()
                : [(value!["status"]?.GetValue<int>() ?? 200, value["body"]!.ToJsonString())];
        }

        foreach (var (key, body) in host.HarnessReplies) replies.TryAdd(key, [(200, body)]);
        host.Broker.Reset(replies);

        await host.PrepareAsync(f);
        if (host.IgnoredReads.Count > 0)
        {
            var recorded = f["sent"]!.AsArray();
            foreach (var read in recorded.OfType<JsonObject>().Where(r => host.IgnoredReads.Contains(r["path"]!.GetValue<string>())).ToList()) recorded.Remove(read);
        }

        var apiKey = f["analyze"] is JsonValue analyze && analyze.GetValue<bool>() ? host.PaperApiKey : host.ApiKey;
        var request = (JsonObject)f["request"]!.DeepClone();
        if (request["apikey"] is JsonValue k && k.GetValue<string>() == recordedKey) request["apikey"] = apiKey;
        // A broker sign-in is an admin call on an account. The state it must carry comes from starting the sign-in, which sends nothing to the broker.
        var admin = f["auth"]!.GetValue<string>() == "admin";
        var path = f["path"]!.GetValue<string>().Replace("{account}", host.AccountId.ToString());
        var recordedToken = host.Fixtures["auth_token"]!.GetValue<string>();
        if (admin)
        {
            (await host.Host!.Admin.PutAsJsonAsync($"/api/v1/accounts/{host.AccountId}/broker-session", new { auth_token = recordedToken })).EnsureSuccessStatusCode();
            if (request["state"] is JsonValue issued && issued.GetValue<string>() == "<issued>")
            {
                using var started = await host.Host.Admin.PostAsync($"/api/v1/accounts/{host.AccountId}/broker-login/start", null);
                request["state"] = JsonNode.Parse(await started.Content.ReadAsStringAsync())!["state"]!.GetValue<string>();
            }
        }

        using var anonymous = admin ? null : host.Host!.Anonymous();
        var client = admin ? host.Host!.Admin : anonymous!;
        var isGet = f["method"]?.GetValue<string>() == "GET";
        // Without auth the request carries no key of the install's (a key it does carry is a wrong one, sent as recorded).
        if (f["auth"]!.GetValue<string>() == "none" && request["apikey"] is JsonValue own && own.GetValue<string>() == apiKey) request.Remove("apikey");
        using var message = isGet
            ? new HttpRequestMessage(HttpMethod.Get, path + "?" + string.Join("&", request.Where(kv => kv.Value is not null).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!.ToString())}")))
            : new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        if (f["auth"]!.GetValue<string>() == "header") message.Headers.Add("X-API-KEY", apiKey);
        using var response = await client.SendAsync(message);
        var answerText = await response.Content.ReadAsStringAsync();
        // A plain-text answer (the ticker's txt format) is compared as text; every other answer is JSON.
        var textAnswer = f["expected"]!["text"] is not null;
        var actual = textAnswer ? null : JsonNode.Parse(answerText);
        output.WriteLine($"EP Trade bot answered {(int)response.StatusCode}: {(textAnswer ? answerText : actual?.ToJsonString())}");
        var sent = host.Broker.Sent;
        foreach (var request1 in sent) host.Verify(request1);
        bool IsRead(SentRequest s) => s.Method == "GET" || readPosts.Contains(s.Path);

        if (deviations.TryGetValue(name, out var deviation))
        {
            output.WriteLine($"Deliberate difference: {deviation.Reason}");
            if (deviation.Adjust is null)
            {
                Assert.Equal(deviation.Status, (int)response.StatusCode);
                if (deviation.Status != 200) Assert.DoesNotContain(sent, s => !IsRead(s));
                return;
            }

            deviation.Adjust(f);
        }

        var expected = f["expected"]!;
        Assert.Equal(expected["status"]!.GetValue<int>(), (int)response.StatusCode);
        if (f.ContainsKey("stored")) await AssertStoredSessionAsync(host, f, expected["status"]!.GetValue<int>() == 200, recordedToken);
        if (expected["text"] is JsonValue expectedText)
        {
            Assert.Equal(expectedText.GetValue<string>(), answerText);
            Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
            CompareTraffic(host, f, sent, IsRead, readPosts, replies.Keys);
            return;
        }

        if (IsSchemaMessage(expected))
        {
            // Validation wording comes from marshmallow; the contract is the status and the error shape.
            Assert.Equal("error", actual?["status"]?.GetValue<string>());
            if (expected["body"]?["errors"] is JsonObject fields)
            {
                // The option endpoints' shape: "Validation error" with the failing fields in "errors".
                Assert.Equal("Validation error", actual?["message"]?.GetValue<string>());
                Assert.Equal(fields.Select(kv => kv.Key).Order(), (actual?["errors"] as JsonObject ?? []).Select(kv => kv.Key).Order());
            }
            else if (expected["body"]?["message"] is JsonObject named)
            {
                Assert.Equal(named.Select(kv => kv.Key).Order(), (actual?["message"] as JsonObject ?? []).Select(kv => kv.Key).Order());
            }

            Assert.Empty(sent);
            return;
        }

        // The instant of answering (/optionchain's server_ts) is checked against the clock, not against the recording.
        if (expected["body"] is JsonObject eb && eb.ContainsKey("server_ts") && actual is JsonObject ab)
        {
            var now = (host.RecordedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            Assert.True(ab["server_ts"] is JsonValue ts && Math.Abs(ts.GetValue<long>() - now) < 300, $"server_ts {ab["server_ts"]} is not the time of answering");
            eb["server_ts"] = ab["server_ts"]!.DeepClone();
        }

        var diffs = JsonCompare.Differences(expected["body"], actual);
        Assert.True(diffs.Count == 0, "Response differs from OpenAlgo:\n" + string.Join("\n", diffs));
        CompareTraffic(host, f, sent, IsRead, readPosts, replies.Keys);
    }

    /// <summary>After a sign-in, the account holds the tokens the broker issued; after a refused one, the tokens it had.</summary>
    private static async Task AssertStoredSessionAsync(BrokerContractHost host, JsonObject f, bool signedIn, string previousToken)
    {
        await using var db = host.Host!.NewDb();
        var account = await db.BrokerAccounts.AsNoTracking().SingleAsync(a => a.Id == host.AccountId);
        var session = host.Host.Get<EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory>().Create(account);
        Assert.NotNull(session);
        if (signedIn)
        {
            Assert.Equal(f["stored"]!["auth_token"]!.GetValue<string>(), session!.AuthToken);
            Assert.Equal(f["stored"]!["feed_token"]?.GetValue<string>(), session.FeedToken);
        }
        else
        {
            Assert.Equal(previousToken, session!.AuthToken);
        }
    }

    private static bool IsSchemaMessage(JsonNode expected) =>
        expected["status"]!.GetValue<int>() == 400 && expected["body"]?["message"] is { } m
        && (m is JsonObject || (m is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('{')) || expected["body"]?["errors"] is JsonObject);

    private static void CompareTraffic(BrokerContractHost host, JsonObject f, IReadOnlyList<SentRequest> actualSent, Func<SentRequest, bool> isRead,
        HashSet<string> readPosts, IEnumerable<string> answered)
    {
        var expectedSent = f["sent"]!.AsArray().OfType<JsonObject>().ToList();
        bool ExpectedRead(JsonObject s) => s["method"]!.GetValue<string>() == "GET" || readPosts.Contains(s["path"]!.GetValue<string>());

        var expectedWrites = expectedSent.Where(s => !ExpectedRead(s)).ToList();
        var actualWrites = actualSent.Where(s => !isRead(s)).ToList();
        Assert.True(expectedWrites.Count == actualWrites.Count,
            $"Expected {expectedWrites.Count} writes to the broker, EP Trade bot sent {actualWrites.Count}: {string.Join(", ", actualWrites.Select(w => $"{w.Method} {w.Path}"))}");
        for (var i = 0; i < expectedWrites.Count; i++)
        {
            var e = expectedWrites[i];
            var a = actualWrites[i];
            Assert.Equal(e["method"]!.GetValue<string>(), a.Method);
            Assert.Equal(e["path"]!.GetValue<string>(), a.Path);
            Assert.Equal(e["host"]!.GetValue<string>(), a.Host);
            Assert.Equal(e["query"]!.AsArray().Select(p => $"{p![0]}={p[1]}"), a.Query.Select(q => $"{q.Key}={q.Value}"));
            CompareHeaders(e, a);
            CompareBody(host, e, a, $"Write {i} ({a.Method} {a.Path})");
        }

        var answeredSet = answered.ToHashSet();
        foreach (var e in expectedSent.Where(ExpectedRead))
        {
            var path = e["path"]!.GetValue<string>();
            var query = e["query"]!.AsArray().Select(p => $"{p![0]}={p[1]}").ToList();
            // Headers too (credentials, client id); a GET carries no body, so its content type is not compared.
            var matches = actualSent.Where(a => isRead(a) && a.Method == e["method"]!.GetValue<string>() && a.Path == path && a.Host == e["host"]!.GetValue<string>()
                && a.Query.Select(q => $"{q.Key}={q.Value}").SequenceEqual(query)
                && HeadersMatch(e, a, ignoreContentType: a.Method == "GET")).ToList();
            Assert.True(matches.Count > 0,
                $"Expected a read of {path}?{string.Join("&", query)}; EP Trade bot read: {string.Join(", ", actualSent.Where(isRead).Select(s => s.Path + "?" + string.Join("&", s.Query.Select(q => $"{q.Key}={q.Value}"))))}");
            if (e["json"] is not null)
            {
                var expectedBody = Masked(host, "json", e["json"]);
                Assert.True(matches.Any(a => JsonCompare.Differences(expectedBody, Masked(host, "json", a.Json), numbersMayBeText: host.NumbersMayBeText).Count == 0),
                    $"No read of {path} carried the body {e["json"]!.ToJsonString()}; EP Trade bot sent: {string.Join(" | ", matches.Select(m => m.Json?.ToJsonString()))}");
            }
        }

        foreach (var a in actualSent.Where(isRead))
            Assert.True(answeredSet.Contains($"{a.Method} {a.Path}"), $"EP Trade bot read {a.Method} {a.Path}, which the Python implementation never reads here.");
    }

    /// <summary>A copy with the volatile fields replaced by a marker, after checking each has its expected format.</summary>
    private static JsonNode? Masked(BrokerContractHost host, string kind, JsonNode? body)
    {
        if (body is not JsonObject obj) return body?.DeepClone();
        var copy = (JsonObject)obj.DeepClone();
        foreach (var (field, pattern) in host.VolatileFields)
        {
            if (!field.StartsWith(kind + ".", StringComparison.Ordinal)) continue;
            var key = field[(kind.Length + 1)..];
            if (copy[key] is JsonValue v && v.TryGetValue<string>(out var text))
            {
                Assert.Matches(pattern, text);
                copy[key] = "<volatile>";
            }
        }

        return copy;
    }

    private static void CompareBody(BrokerContractHost host, JsonObject expected, SentRequest actual, string label)
    {
        if (expected["form"] is JsonObject form)
        {
            Assert.NotNull(actual.Form);
            var actualForm = actual.Form!.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal(form.Select(kv => kv.Key).OrderBy(x => x), actualForm.Keys.OrderBy(x => x));
            foreach (var (key, value) in form)
            {
                if (host.VolatileFields.TryGetValue("form." + key, out var pattern))
                {
                    Assert.Matches(pattern, actualForm[key]);
                    continue;
                }

                var ev = value!.GetValue<string>();
                Assert.True(JsonCompare.SameValue(ev, actualForm[key]), $"{label} field {key}: expected \"{ev}\", got \"{actualForm[key]}\"");
            }
        }
        else if (expected["json"] is { } json)
        {
            var diffs = JsonCompare.Differences(Masked(host, "json", json), Masked(host, "json", actual.Json), numbersMayBeText: host.NumbersMayBeText);
            Assert.True(diffs.Count == 0, $"{label} body differs from OpenAlgo's:\n" + string.Join("\n", diffs));
        }
    }

    private static bool HeadersMatch(JsonObject expected, SentRequest actual, bool ignoreContentType)
    {
        foreach (var (key, value) in expected["headers"]!.AsObject())
        {
            var isType = key.Equals("content-type", StringComparison.OrdinalIgnoreCase);
            if (isType && ignoreContentType) continue;
            // Python's HTTP client adds Accept: */* by itself; a value the plugin chose is compared.
            if (key.Equals("accept", StringComparison.OrdinalIgnoreCase) && value!.GetValue<string>() == "*/*") continue;
            if (!actual.Headers.TryGetValue(key, out var av)) return false;
            if (isType ? !av.StartsWith(value!.GetValue<string>(), StringComparison.OrdinalIgnoreCase) : av != value!.GetValue<string>()) return false;
        }

        return true;
    }

    private static void CompareHeaders(JsonObject expected, SentRequest actual)
    {
        foreach (var (key, value) in expected["headers"]!.AsObject())
        {
            var ev = value!.GetValue<string>();
            if (key.Equals("accept", StringComparison.OrdinalIgnoreCase) && ev == "*/*") continue;
            Assert.True(actual.Headers.TryGetValue(key, out var av), $"Header {key} missing");
            if (key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) Assert.StartsWith(ev, av, StringComparison.OrdinalIgnoreCase);
            else Assert.Equal(ev, av);
        }
    }

    /// <summary>Sets an expected answer: the status and the JSON body.</summary>
    public static void Expect(JsonObject fixture, int status, JsonObject body)
    {
        fixture["expected"]!["status"] = status;
        fixture["expected"]!["body"] = body;
    }

    /// <summary>The first recorded write with this method.</summary>
    public static JsonObject Write(JsonObject fixture, string method, int index = 0) =>
        fixture["sent"]!.AsArray().OfType<JsonObject>().Where(s => s["method"]!.GetValue<string>() == method).ElementAt(index);

    public static JsonObject Error(string message) => new() { ["status"] = "error", ["message"] = message };

    internal static bool Like(string pattern, string text) => Regex.IsMatch(text, pattern);
}
