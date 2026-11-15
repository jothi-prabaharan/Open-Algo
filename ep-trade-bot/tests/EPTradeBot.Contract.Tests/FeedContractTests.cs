using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Contract.Tests;

/// <summary>
/// Market-data feed codecs against the Python adapters (tools/contract-fixtures/feed_*.fixtures.json). Each scenario is a list of steps (subscribe,
/// unsubscribe, reconnect, frame); the messages sent to the broker and the ticks published for each step must match what the real adapter produced.
/// </summary>
public static class FeedRunner
{
    /// <summary>A message with its id replaced by a stand-in (the id is new every time by design).</summary>
    private static string Stand(string message)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(message);
        }
        catch (System.Text.Json.JsonException)
        {
            // A message that is plain text (a sign-in such as LOGIN:token) stands for itself.
            return message;
        }

        if (node is JsonObject o && o["guid"] is not null) o["guid"] = "<guid>";
        return node!.ToJsonString();
    }

    /// <summary>The key a message sorts by: the JSON it is, or the JSON text a plain message would be recorded as.</summary>
    private static string SortKey(string message)
    {
        try
        {
            return JsonNode.Parse(message)!.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(message)!.ToJsonString();
        }
    }

    public static IEnumerable<object[]> Names(string file) =>
        BrokerContractHost.Load(file)["scenarios"]!.AsArray().Select(s => new object[] { s!["name"]!.GetValue<string>() });

    /// <param name="adjust">A deliberate difference: the recorded scenario is changed to the answer EP Trade bot gives instead, then compared in full.</param>
    public static void Run(string file, string name, Func<IFeedCodec> codec, Action<JsonObject>? adjust = null, bool anyOrderAcrossInstruments = false, bool dropRepeatedRequests = false)
    {
        var fixtures = BrokerContractHost.Load(file);
        var now = fixtures["now_ms"]!.GetValue<long>();
        var symbols = fixtures["symbols"]!.AsArray().OfType<JsonObject>().ToDictionary(s => (s["exchange"]!.GetValue<string>(), s["symbol"]!.GetValue<string>()), s => (Token: s["token"]!.GetValue<string>(), BrokerExchange: s["brexchange"]!.GetValue<string>(), BrokerSymbol: s["brsymbol"]!.GetValue<string>(), Name: s["name"]?.GetValue<string>()));
        var scenario = (JsonObject)fixtures["scenarios"]!.AsArray().OfType<JsonObject>().Single(s => s["name"]!.GetValue<string>() == name).DeepClone();
        adjust?.Invoke(scenario);
        var feed = codec();
        if (fixtures["access_token"] is JsonValue token) feed.Begin(new BrokerSession(Guid.Empty, fixtures["broker"]!.GetValue<string>(), token.GetValue<string>(), null, fixtures["client_id"] is JsonValue client ? client.GetValue<string>() : null, new Dictionary<string, string>()));
        // A scenario that starts signed in: the sign-in answer arrives before the first step.
        foreach (var start in (fixtures["start_frames"] as JsonArray ?? []).Select(x => x!.GetValue<string>()))
        {
            feed.DecodeBinary(Convert.FromHexString(start), now);
            feed.TakeOutgoing();
        }

        var index = 0;
        foreach (var entry in scenario["steps"]!.AsArray().OfType<JsonObject>())
        {
            var step = entry["step"]!.AsObject();
            var result = entry["result"]!.AsObject();
            var kind = step.First().Key;
            var label = $"{name} step {++index} ({kind})";
            IReadOnlyList<string> sent = [];
            IReadOnlyList<FeedPublish> published = [];
            switch (kind)
            {
                case "subscribe":
                    sent = feed.Subscribe(step["subscribe"]!.AsArray().OfType<JsonObject>().Select(i =>
                    {
                        var (exchange, symbol) = (i["exchange"]!.GetValue<string>(), i["symbol"]!.GetValue<string>());
                        var (token, brokerExchange, brokerSymbol, name) = symbols[(exchange, symbol)];
                        return new FeedRequest(exchange, symbol, token, brokerExchange, (StreamMode)i["mode"]!.GetValue<int>(), brokerSymbol, name);
                    }).ToList(), now);
                    break;
                case "unsubscribe":
                    var u = step["unsubscribe"]!.AsObject();
                    var (uExchange, uSymbol) = (u["exchange"]!.GetValue<string>(), u["symbol"]!.GetValue<string>());
                    var (uToken, uBrokerExchange, uBrokerSymbol, uName) = symbols[(uExchange, uSymbol)];
                    sent = feed.Unsubscribe(new FeedTarget(uExchange, uSymbol, uToken, uBrokerExchange, uBrokerSymbol, uName), u["mode"] is JsonValue m ? (StreamMode)m.GetValue<int>() : null, now);
                    break;
                case "reconnect":
                    sent = feed.Resubscribe(now);
                    break;
                case "signed_out":
                    feed.Resubscribe(now);
                    break;
                case "frame":
                    published = feed.DecodeBinary(Convert.FromHexString(step["frame"]!.GetValue<string>()), now);
                    sent = feed.TakeOutgoing();
                    break;
                case "text":
                    // A text frame is recorded as the JSON it carried, or as the raw text when that was not JSON.
                    published = feed.DecodeText(step["text"] is JsonValue raw && raw.TryGetValue<string>(out var rawText) ? rawText : step["text"]!.ToJsonString(), now);
                    sent = feed.TakeOutgoing();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown step {kind}.");
            }

            // The messages: JSON, compared by value and in order.
            var expectedSent = result["sent"]!.AsArray();
            if (kind == "reconnect" && dropRepeatedRequests)
            {
                // OpenAlgo sends each request twice on a reconnect (once from the socket client's list, once from the adapter's); one of each is the same state.
                expectedSent = new JsonArray(expectedSent.GroupBy(x => x!.ToJsonString()).Select(g => g.First()!.DeepClone()).ToArray());
            }

            if (kind == "reconnect" && !feed.MessagesAreHex)
            {
                // OpenAlgo walks a Python set to repeat its subscriptions, whose order means nothing: the same messages, in any order, are the same.
                expectedSent = new JsonArray(expectedSent.OrderBy(x => x!.ToJsonString(), StringComparer.Ordinal).Select(x => x!.DeepClone()).ToArray());
                sent = sent.Select(x => Stand(x)).OrderBy(SortKey, StringComparer.Ordinal).ToList();
            }

            Assert.True(expectedSent.Count == sent.Count, $"{label}: expected {expectedSent.Count} messages to the broker, got {sent.Count}: {string.Join(" | ", sent)}");
            for (var i = 0; i < sent.Count; i++)
            {
                // A protocol of raw bytes records each message as hex text.
                if (expectedSent[i] is JsonValue hex && hex.TryGetValue<string>(out var expectedHex))
                {
                    Assert.True(string.Equals(expectedHex, sent[i], StringComparison.OrdinalIgnoreCase), $"{label}: message {i} differs:\nexpected {expectedHex}\nactual   {sent[i]}");
                    continue;
                }

                // A message id is new every time by design; the recording holds a stand-in.
                var actual = JsonNode.Parse(sent[i]);
                if (actual is JsonObject { } message && message["guid"] is not null) message["guid"] = "<guid>";
                var diffs = JsonCompare.Differences(expectedSent[i], actual);
                Assert.True(diffs.Count == 0, $"{label}: message {i} differs:\n{string.Join("\n", diffs)}");
            }

            // The ticks: same topics in the same order, same data.
            var expectedPublished = result["published"]!.AsArray();
            if (anyOrderAcrossInstruments)
            {
                // A frame's instruments come out of a hash map on the Python side, whose order means nothing: the same ticks, in any order, are the same.
                expectedPublished = new JsonArray(expectedPublished.OrderBy(x => x!["topic"]!.GetValue<string>(), StringComparer.Ordinal).Select(x => x!.DeepClone()).ToArray());
                published = published.OrderBy(p => p.Topic, StringComparer.Ordinal).ToList();
            }

            Assert.True(expectedPublished.Count == published.Count,
                $"{label}: expected {expectedPublished.Count} ticks, got {published.Count}: {string.Join(", ", published.Select(p => p.Topic))}");
            for (var i = 0; i < published.Count; i++)
            {
                Assert.Equal(expectedPublished[i]!["topic"]!.GetValue<string>(), published[i].Topic);
                var diffs = JsonCompare.Differences(expectedPublished[i]!["data"], published[i].Data);
                Assert.True(diffs.Count == 0, $"{label}: tick {published[i].Topic} differs:\n{string.Join("\n", diffs)}");
            }
        }
    }
}

public sealed class ZerodhaFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_zerodha.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_zerodha.fixtures.json", name, () => new KiteFeedCodec());
}

public sealed class AngelFeedContractTests
{
    /// <summary>
    /// OpenAlgo reads a depth tick's open, high, low and close under names the stream parser never sets, so every depth subscriber gets zeros there.
    /// EP Trade bot reads the right fields: the same open, high, low and close a quote tick carries.
    /// </summary>
    private static readonly Dictionary<string, (double Open, double High, double Low, double Close)> DepthOhlc = new()
    {
        ["snap_quote_frame_with_depth"] = (800.0, 810.0, 795.5, 799.95),
        ["snap_quote_frame_with_one_sided_depth"] = (800.0, 810.0, 795.5, 799.95),
        ["snap_quote_frame_on_a_futures_contract"] = (800.0, 810.0, 795.5, 799.95),
    };

    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_angel.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_angel.fixtures.json", name, () => new SmartApiFeedCodec(),
        DepthOhlc.TryGetValue(name, out var ohlc)
            ? scenario =>
            {
                foreach (var tick in scenario["steps"]!.AsArray().SelectMany(st => st!["result"]!["published"]!.AsArray()).Select(p => p!["data"]!.AsObject()))
                {
                    tick["open"] = ohlc.Open;
                    tick["high"] = ohlc.High;
                    tick["low"] = ohlc.Low;
                    tick["close"] = ohlc.Close;
                }
            }
            : null);
}

public sealed class PaytmFeedContractTests
{
    /// <summary>
    /// OpenAlgo drops a token's whole entry when any one of its modes is unsubscribed, so a client that unsubscribes LTP stops getting Quote ticks for
    /// the same instrument. EP Trade bot keeps the token known while any mode of it is subscribed.
    /// </summary>
    private static readonly HashSet<string> TokenKeptWhileAnyModeRemains = ["frame_after_one_of_two_modes_is_unsubscribed"];

    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_paytm.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_paytm.fixtures.json", name, () => new PaytmFeedCodec(),
        TokenKeptWhileAnyModeRemains.Contains(name)
            ? scenario =>
            {
                // The same quote frame, answered with the quote tick the client still subscribes to (OpenAlgo answers with nothing).
                var both = BrokerContractHost.Load("feed_paytm.fixtures.json")["scenarios"]!.AsArray().OfType<JsonObject>().Single(x => x["name"]!.GetValue<string>() == "two_modes_of_one_token");
                scenario["steps"]![2]!["result"]!["published"] = both["steps"]![2]!["result"]!["published"]!.DeepClone();
            }
            : null);
}

public sealed class DeltaFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_deltaexchange.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_deltaexchange.fixtures.json", name, () => new DeltaFeedCodec());
}

public sealed class UpstoxFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_upstox.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_upstox.fixtures.json", name, () => new UpstoxFeedCodec(), anyOrderAcrossInstruments: true);
}

public sealed class DhanFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_dhan.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_dhan.fixtures.json", name, () => new DhanFeedCodec(), dropRepeatedRequests: true);
}

public sealed class FyersFeedContractTests
{
    private const string SbinFeedRequest = "0017040201001100010e73667c6e73655f636d7c333034350200010b";

    /// <summary>
    /// OpenAlgo drops a subscription made before the sign-in is accepted (the feed names are not remembered), and gives a feed it cannot place to the only
    /// subscription there is. EP Trade bot holds the feed names until the sign-in is accepted, and does not guess.
    /// </summary>
    private static readonly Dictionary<string, Action<JsonObject>> Differences = new()
    {
        ["a_subscription_before_the_sign_in_is_accepted_waits"] = scenario => scenario["steps"]![2]!["result"]!["sent"] = new JsonArray(SbinFeedRequest),
        ["a_subscription_after_a_reconnect_but_before_the_answer_waits"] = scenario => scenario["steps"]![2]!["result"]!["sent"] = new JsonArray(SbinFeedRequest),
        ["a_feed_nobody_asked_for_is_not_guessed_at_even_with_one_subscription"] = scenario => scenario["steps"]![1]!["result"]!["published"] = new JsonArray(),
    };

    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_fyers.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_fyers.fixtures.json", name, () => new FyersFeedCodec(), Differences.GetValueOrDefault(name));
}

public sealed class ShoonyaFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_shoonya.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_shoonya.fixtures.json", name, SignedIn,
        name == "unsubscribe_while_signed_out"
            ? scenario =>
            {
                // OpenAlgo refuses an unsubscribe while the socket is signed out and keeps the instrument, so the next sign-in subscribes it again. EP Trade bot
                // lets go of it on the spot, so the trader's unsubscribe is not undone by a reconnect.
                scenario["steps"]![4]!["result"]!["sent"]!.AsArray().RemoveAt(0);
            }
            : null,
        anyOrderAcrossInstruments: true);

    [Fact]
    public void A_reset_forgets_every_subscription()
    {
        var codec = SignedIn();
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 0);
        codec.Reset();
        codec.Resubscribe(0);
        codec.DecodeText("""{"t":"ak","s":"OK"}""", 0);
        Assert.Empty(codec.TakeOutgoing());
    }

    /// <summary>The recording starts with the socket signed in; so does the codec.</summary>
    private static IFeedCodec SignedIn()
    {
        var codec = new NorenFeedCodec("shoonya");
        codec.Begin(new BrokerSession(Guid.Empty, "shoonya", "shoonya-session-token", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA12345:::shoonya-api-key" }));
        codec.DecodeText("""{"t":"ak","s":"OK"}""", 0);
        codec.TakeOutgoing();
        return codec;
    }
}

public sealed class ZebuFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_zebu.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_zebu.fixtures.json", name, SignedIn, Adjustments.GetValueOrDefault(name), anyOrderAcrossInstruments: true);

    private static Action<JsonObject> Sends(int step, params string[] messages) =>
        scenario => scenario["steps"]![step]!["result"]!["sent"] = new JsonArray(messages.Select(m => (JsonNode?)JsonNode.Parse(m)).ToArray());

    /// <summary>
    /// OpenAlgo counts every subscribe call of one instrument and releases it from the broker only when the count falls to zero, which a second identical call
    /// prevents for good (the instrument stays subscribed until the socket drops). EP Trade bot's host holds one subscription per instrument and counts its
    /// clients itself, so the codec releases the instrument when its one hold goes. And OpenAlgo knows when the socket is signed out and sends nothing then;
    /// EP Trade bot's connection discards what the codec says while it is down, and the next sign-in sends everything held.
    /// </summary>
    public static readonly Dictionary<string, Action<JsonObject>> Adjustments = new()
    {
        ["unsubscribe_one_of_two_owners_sends_nothing"] = Sends(2, """{"t":"u","k":"NSE|3045"}"""),
        ["subscribe_while_signed_out_sends_nothing_until_sign_in"] = Sends(1, """{"t":"t","k":"NSE|3045"}""", """{"t":"d","k":"NSE|1594"}"""),
        ["unsubscribe_while_signed_out"] = Sends(2, """{"t":"u","k":"NSE|3045"}"""),
    };

    private static IFeedCodec SignedIn()
    {
        var codec = new NorenFeedCodec("zebu");
        codec.Begin(new BrokerSession(Guid.Empty, "zebu", "zebu-session-token", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA12345:::zebu-client-id" }));
        codec.Resubscribe(0);
        return codec;
    }
}

public sealed class FlattradeFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_flattrade.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_flattrade.fixtures.json", name, SignedIn, ZebuFeedContractTests.Adjustments.GetValueOrDefault(name), anyOrderAcrossInstruments: true);

    private static IFeedCodec SignedIn()
    {
        var codec = new NorenFeedCodec("flattrade");
        codec.Begin(new BrokerSession(Guid.Empty, "flattrade", "flattrade-session-token", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA12345:::flattrade-api-key" }));
        codec.Resubscribe(0);
        return codec;
    }
}

/// <summary>What the recordings cannot hold: OpenAlgo repeats its subscriptions from a Python set, whose order changes with every run.</summary>
public sealed class NorenResubscribeTests
{
    private static NorenFeedCodec Codec(string broker, int count)
    {
        var codec = new NorenFeedCodec(broker);
        codec.Begin(new BrokerSession(Guid.Empty, broker, "token", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA12345:::key" }));
        codec.Resubscribe(0);
        codec.DecodeText("""{"t":"ak","s":"OK"}""", 0);
        codec.TakeOutgoing();
        codec.Subscribe(Enumerable.Range(0, count).Select(i => new FeedRequest("NSE", $"S{i}", $"{5000 + i}", "NSE", StreamMode.Quote)).ToList(), 0);
        return codec;
    }

    private static int Scrips(string message) => System.Text.Json.Nodes.JsonNode.Parse(message)!["k"]!.GetValue<string>().Split('#').Length;

    [Fact]
    public void Shoonya_repeats_a_hundred_and_one_subscriptions_in_two_messages()
    {
        var codec = Codec("shoonya", 101);
        codec.Resubscribe(0);
        codec.DecodeText("""{"t":"ak","s":"OK"}""", 0);
        Assert.Equal([100, 1], codec.TakeOutgoing().Select(Scrips));
    }

    [Theory]
    [InlineData("zebu")]
    [InlineData("flattrade")]
    public void Zebu_and_Flattrade_repeat_every_subscription_in_one_message(string broker)
    {
        var codec = Codec(broker, 101);
        var messages = codec.Resubscribe(0);
        Assert.Equal(2, messages.Count);
        Assert.Equal(101, Scrips(messages[1]));
    }
}

/// <summary>The XTS feed against the Python adapters: the HTTP calls that subscribe, and the ticks for what the socket and the replies carry.</summary>
public static class XtsFeedRunner
{
    public static void Run(string file, string name, Func<IFeedHttpCodec> create, string marketData, Action<JsonObject>? adjust = null, bool stampsCode = false)
    {
        var fixtures = BrokerContractHost.Load(file);
        var now = fixtures["now_ms"]!.GetValue<long>();
        var symbols = fixtures["symbols"]!.AsArray().OfType<JsonObject>().ToDictionary(s => (s["exchange"]!.GetValue<string>(), s["symbol"]!.GetValue<string>()), s => (Token: s["token"]!.GetValue<string>(), BrokerExchange: s["brexchange"]!.GetValue<string>()));
        var scenario = (JsonObject)fixtures["scenarios"]!.AsArray().OfType<JsonObject>().Single(s => s["name"]!.GetValue<string>() == name).DeepClone();
        adjust?.Invoke(scenario);
        var feed = create();
        Open(feed, now);
        var pending = new List<string>();
        var index = 0;
        foreach (var entry in scenario["steps"]!.AsArray().OfType<JsonObject>())
        {
            var step = entry["step"]!.AsObject();
            var kind = step.First().Key;
            var label = $"{name} step {++index} ({kind})";
            var result = entry["result"]!.AsObject();
            var published = new List<FeedPublish>();
            switch (kind)
            {
                case "quotes_next":
                    pending = step["quotes_next"]!.AsArray().Select(q => q is JsonValue v && v.TryGetValue<string>(out var t) ? t : q!.ToJsonString()).ToList();
                    continue;
                case "subscribe":
                    feed.Subscribe(step["subscribe"]!.AsArray().OfType<JsonObject>().Select(i =>
                    {
                        var (exchange, symbol) = (i["exchange"]!.GetValue<string>(), i["symbol"]!.GetValue<string>());
                        return new FeedRequest(exchange, symbol, symbols[(exchange, symbol)].Token, symbols[(exchange, symbol)].BrokerExchange, (StreamMode)i["mode"]!.GetValue<int>());
                    }).ToList(), now);
                    break;
                case "unsubscribe":
                    var u = step["unsubscribe"]!.AsObject();
                    var (ue, us) = (u["exchange"]!.GetValue<string>(), u["symbol"]!.GetValue<string>());
                    feed.Unsubscribe(new FeedTarget(ue, us, symbols[(ue, us)].Token, symbols[(ue, us)].BrokerExchange), (StreamMode)u["mode"]!.GetValue<int>(), now);
                    break;
                case "reconnect":
                    feed.Resubscribe(now);
                    Open(feed, now);
                    break;
                case "event":
                    var e = step["event"]!.AsObject();
                    published.AddRange(feed.DecodeText("42" + new JsonArray(e["name"]!.GetValue<string>(), e["payload"]!.DeepClone()).ToJsonString(), now));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown step {kind}.");
            }

            var calls = feed.TakeHttp();
            if (calls.Count > 0)
            {
                foreach (var quote in pending)
                {
                    var text = quote;
                    // RMoney's plugin gives a quote that names no message code the code of the request it answers.
                    if (stampsCode && quote.StartsWith('{') && JsonNode.Parse(quote) is JsonObject q && !q.ContainsKey("MessageCode"))
                    {
                        q["MessageCode"] = calls[^1].Body["xtsMessageCode"]!.DeepClone();
                        text = q.ToJsonString();
                    }

                    published.AddRange(feed.DecodeText(text, now));
                }
                pending = [];
            }

            var expected = result["http"]!.AsArray();
            Assert.True(expected.Count == calls.Count, $"{label}: expected {expected.Count} calls, got {calls.Count}: {string.Join(" | ", calls.Select(c => c.Method + " " + c.Body.ToJsonString()))}");
            for (var i = 0; i < calls.Count; i++)
            {
                Assert.Equal(expected[i]!["method"]!.GetValue<string>(), calls[i].Method);
                Assert.Equal(expected[i]!["path"]!.GetValue<string>(), marketData + calls[i].Path);
                var diffs = JsonCompare.Differences(expected[i]!["body"], calls[i].Body);
                Assert.True(diffs.Count == 0, $"{label}: call {i} differs:\n{string.Join("\n", diffs)}");
            }

            var ticks = result["published"]!.AsArray();
            Assert.True(ticks.Count == published.Count, $"{label}: expected {ticks.Count} ticks, got {published.Count}: {string.Join(", ", published.Select(p => p.Topic))}");
            for (var i = 0; i < published.Count; i++)
            {
                Assert.Equal(ticks[i]!["topic"]!.GetValue<string>(), published[i].Topic);
                var diffs = JsonCompare.Differences(ticks[i]!["data"], published[i].Data);
                Assert.True(diffs.Count == 0, $"{label}: tick {published[i].Topic} differs:\n{string.Join("\n", diffs)}");
            }
        }
    }

    /// <summary>The socket opens: the server's open packet is answered with a Socket.IO connect, and its acceptance releases the subscriptions held.</summary>
    private static void Open(IFeedHttpCodec feed, long now)
    {
        feed.DecodeText("""0{"sid":"abc","upgrades":[],"pingInterval":25000,"pingTimeout":5000}""", now);
        Assert.Equal(["40"], feed.TakeOutgoing());
        feed.DecodeText("""40{"sid":"xyz"}""", now);
    }
}

public sealed class WisdomFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_wisdom.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => XtsFeedRunner.Run("feed_wisdom.fixtures.json", name, () => new XtsFeedCodec("wisdom"), "/apimarketdata", XtsFeedDeviations.Common.GetValueOrDefault(name));
}

public static class XtsFeedDeviations
{
    private static Action<JsonObject> NoTick(int step) => scenario => scenario["steps"]![step]!["result"]!["published"] = new JsonArray();

    /// <summary>
    /// OpenAlgo publishes whatever tick arrives, under the topic its message code names, even for a mode or an instrument nobody subscribed; EP Trade bot
    /// publishes only to what is subscribed. And OpenAlgo asks to release a depth subscription with the quote code (it looks the code up under a name that carries
    /// the depth level, finds nothing and falls back to 1501), so the depth subscription is never released; EP Trade bot sends the depth code.
    /// </summary>
    public static readonly Dictionary<string, Action<JsonObject>> Common = new()
    {
        ["an_event_for_another_mode_is_ignored"] = NoTick(1),
        ["an_event_for_an_instrument_nobody_subscribed"] = NoTick(1),
        ["unsubscribe_quote_and_depth"] = scenario => scenario["steps"]![2]!["result"]!["http"]![0]!["body"]!["xtsMessageCode"] = 1502,
    };
}

public sealed class IiflFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_iifl.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => XtsFeedRunner.Run("feed_iifl.fixtures.json", name, () => new XtsFeedCodec("iifl"), "/apimarketdata", XtsFeedDeviations.Common.GetValueOrDefault(name));
}

public sealed class CompositEdgeFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_compositedge.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => XtsFeedRunner.Run("feed_compositedge.fixtures.json", name, () => new XtsFeedCodec("compositedge"), "/apimarketdata", XtsFeedDeviations.Common.GetValueOrDefault(name));
}

public sealed class IbullsFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_ibulls.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => XtsFeedRunner.Run("feed_ibulls.fixtures.json", name, () => new XtsFeedCodec("ibulls"), "/apibinarymarketdata", XtsFeedDeviations.Common.GetValueOrDefault(name));
}

public sealed class FivepaisaXtsFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_fivepaisaxts.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => XtsFeedRunner.Run("feed_fivepaisaxts.fixtures.json", name, () => new XtsFeedCodec("fivepaisaxts", batch: 50), "//apimarketdata", XtsFeedDeviations.Common.GetValueOrDefault(name));
}

public sealed class JainamXtsFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_jainamxts.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => XtsFeedRunner.Run("feed_jainamxts.fixtures.json", name, () => new XtsFeedCodec("jainamxts", routesByInstrument: true), "/apibinarymarketdata", name == "an_event_for_another_mode_is_ignored" ? null : XtsFeedDeviations.Common.GetValueOrDefault(name));
}

/// <summary>The parts of the XTS protocol the recordings start past (the connection is open in every one) and the places EP Trade bot is deliberately stricter.</summary>
public sealed class XtsFeedCodecTests
{
    private static FeedRequest Request(string symbol = "RELIANCE", string exchange = "NSE", string token = "2885", StreamMode mode = StreamMode.Quote, string brokerExchange = "NSE") =>
        new(exchange, symbol, token, brokerExchange, mode);

    private static XtsFeedCodec Open(bool connect = true)
    {
        var codec = new XtsFeedCodec("wisdom");
        codec.DecodeText("""0{"sid":"a"}""", 0);
        codec.TakeOutgoing();
        if (connect) codec.DecodeText("""40{"sid":"b"}""", 0);
        return codec;
    }

    [Fact]
    public void Nothing_is_sent_until_the_socket_connect_is_accepted_and_then_everything_held_is()
    {
        var codec = Open(connect: false);
        codec.Subscribe([Request()], 0);
        Assert.Empty(codec.TakeHttp());
        codec.DecodeText("""40{"sid":"b"}""", 0);
        Assert.Single(codec.TakeHttp());
    }

    [Fact]
    public void The_same_subscription_twice_is_held_once()
    {
        var codec = Open(connect: false);
        codec.Subscribe([Request(), Request()], 0);
        codec.DecodeText("""40""", 0);
        Assert.Single(codec.TakeHttp());
    }

    [Fact]
    public void A_server_ping_is_answered_with_a_pong()
    {
        var codec = Open();
        codec.DecodeText("2", 0);
        Assert.Equal(["3"], codec.TakeOutgoing());
    }

    [Fact]
    public void A_dropped_connection_holds_requests_until_the_next_accepted_connect()
    {
        var codec = Open();
        codec.Resubscribe(0);
        codec.Subscribe([Request()], 0);
        Assert.Empty(codec.TakeHttp());
        var other = Open();
        other.DecodeText("41", 0);
        other.Subscribe([Request()], 0);
        Assert.Empty(other.TakeHttp());
    }

    [Fact]
    public void An_unsubscribe_without_a_mode_releases_every_mode()
    {
        var codec = Open();
        codec.Subscribe([Request(mode: StreamMode.Ltp), Request(mode: StreamMode.Quote), Request(mode: StreamMode.Depth)], 0);
        codec.TakeHttp();
        codec.Unsubscribe(Request().Target, null, 0);
        Assert.Equal([1512, 1501, 1502], codec.TakeHttp().Select(c => c.Body["xtsMessageCode"]!.GetValue<int>()));
    }

    [Fact]
    public void An_exchange_xts_does_not_have_is_refused_rather_than_sent_as_the_cash_market()
    {
        var codec = Open();
        codec.Subscribe([Request(exchange: "NCO", brokerExchange: "NCO")], 0);
        Assert.Empty(codec.TakeHttp());
    }

    [Fact]
    public void An_event_that_is_not_a_market_data_event_is_not_a_tick()
    {
        var codec = Open();
        codec.Subscribe([Request()], 0);
        var quote = """{"MessageCode":1501,"ExchangeSegment":1,"ExchangeInstrumentID":2885,"LastTradedPrice":1}""";
        Assert.Empty(codec.DecodeText("42[\"message\"," + quote + "]", 0));
        Assert.Single(codec.DecodeText("42[\"1501-json-full\"," + quote + "]", 0));
    }

    [Fact]
    public void A_quote_reads_the_touchline_only_when_it_is_a_depth_message()
    {
        var codec = Open();
        codec.Subscribe([Request()], 0);
        var tick = codec.DecodeText("""42["1501-json-full",{"MessageCode":1501,"ExchangeSegment":1,"ExchangeInstrumentID":2885,"LastTradedPrice":5,"Touchline":{"LastTradedPrice":9}}]""", 0).Single();
        Assert.Equal(5, tick.Data["ltp"]!.GetValue<double>());
    }

    [Fact]
    public void A_book_with_one_side_only_carries_no_depth()
    {
        var codec = Open();
        codec.Subscribe([Request(mode: StreamMode.Depth)], 0);
        var tick = codec.DecodeText("""42["1502-json-full",{"MessageCode":1502,"ExchangeSegment":1,"ExchangeInstrumentID":2885,"Bids":[{"Price":1,"Size":1,"TotalOrders":1}]}]""", 0).Single();
        Assert.Null(tick.Data["depth"]);
    }

    [Fact]
    public void A_tick_that_names_no_message_code_is_a_touchline_on_RMoney_and_nothing_elsewhere()
    {
        var rmoney = new XtsFeedCodec("rmoney", rmoney: true);
        rmoney.DecodeText("40", 0);
        rmoney.Subscribe([Request(mode: StreamMode.Quote)], 0);
        Assert.Equal("QUOTE", rmoney.DecodeText("""{"ExchangeSegment":1,"ExchangeInstrumentID":2885,"LastTradedPrice":1}""", 0).Single().Topic.Split('_')[^1]);
        Assert.Empty(rmoney.DecodeText("""{"MessageCode":"abc","ExchangeSegment":1,"ExchangeInstrumentID":2885}""", 0));
        var other = Open();
        other.Subscribe([Request()], 0);
        Assert.Empty(other.DecodeText("""{"ExchangeSegment":1,"ExchangeInstrumentID":2885,"LastTradedPrice":1}""", 0));
    }

    [Fact]
    public void A_reset_forgets_every_subscription()
    {
        var codec = Open();
        codec.Subscribe([Request()], 0);
        codec.TakeHttp();
        codec.Reset();
        codec.DecodeText("""0{"sid":"a"}""", 0);
        codec.DecodeText("40", 0);
        Assert.Empty(codec.TakeHttp());
    }
}

public sealed class RmoneyFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_rmoney.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => XtsFeedRunner.Run("feed_rmoney.fixtures.json", name, () => new XtsFeedCodec("rmoney", batch: 50, rmoney: true), "/apimarketdata", name == "an_event_for_another_mode_is_ignored" ? null : XtsFeedDeviations.Common.GetValueOrDefault(name), stampsCode: true);
}

public sealed class TradesmartFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_tradesmart.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_tradesmart.fixtures.json", name, SignedIn, ZebuFeedContractTests.Adjustments.GetValueOrDefault(name), anyOrderAcrossInstruments: true);

    private static IFeedCodec SignedIn()
    {
        var codec = new NorenFeedCodec("tradesmart");
        codec.Begin(new BrokerSession(Guid.Empty, "tradesmart", "tradesmart-session-token", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA12345:::tradesmart-api-key" }));
        codec.Resubscribe(0);
        return codec;
    }
}

public sealed class TradesmartFeedSignInTests
{
    [Fact]
    public void The_user_id_saved_in_front_of_the_token_signs_the_socket_in_and_the_token_is_sent_alone()
    {
        var codec = new NorenFeedCodec("tradesmart");
        codec.Begin(new BrokerSession(Guid.Empty, "tradesmart", "TS0001:::tok-1", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA12345:::cid" }));
        var auth = System.Text.Json.Nodes.JsonNode.Parse(codec.Resubscribe(0).Single())!;
        Assert.Equal("TS0001", auth["uid"]!.GetValue<string>());
        Assert.Equal("TS0001", auth["actid"]!.GetValue<string>());
        Assert.Equal("tok-1", auth["accesstoken"]!.GetValue<string>());
    }
}

public sealed class RupeezyFeedContractTests
{
    /// <summary>
    /// OpenAlgo's unsubscribe without a mode removes nothing (it discards the mode "None") and sends the subscribe again at the richest mode, so the
    /// instrument stays subscribed. EP Trade bot ends every mode, and the unsubscribe goes to Vortex.
    /// </summary>
    private static readonly HashSet<string> UnsubscribeWithoutMode = ["unsubscribe_without_a_mode"];

    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_rupeezy.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_rupeezy.fixtures.json", name, () => new VortexFeedCodec(),
        UnsubscribeWithoutMode.Contains(name)
            ? scenario => scenario["steps"]![1]!["result"]!["sent"] = new JsonArray(new JsonObject { ["ticker"] = "NSE:SBIN", ["mode"] = "full", ["message_type"] = "unsubscribe" })
            : null,
        anyOrderAcrossInstruments: true);
}

public sealed class SahiFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_sahi.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_sahi.fixtures.json", name, () => new SahiFeedCodec());
}

public sealed class ArrowFeedContractTests
{
    /// <summary>
    /// OpenAlgo repeats its subscriptions in the order of a Python set of integers, which for a hundred and one tokens starts in the middle of the range. The same tokens in the same
    /// two frames of at most a hundred, in ascending order, are the same state.
    /// </summary>
    private static readonly HashSet<string> SetOrder = ["a_hundred_and_one_tokens_are_repeated_in_two_frames"];

    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_arrow.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_arrow.fixtures.json", name, () => new ArrowFeedCodec(),
        SetOrder.Contains(name)
            ? scenario =>
            {
                var tokens = Enumerable.Range(0, 101).Select(i => 900000L + i).ToList();
                scenario["steps"]![1]!["result"]!["sent"] = new JsonArray(
                    tokens.Chunk(100).Select(c => (JsonNode?)new JsonObject { ["code"] = "sub", ["mode"] = "ltpc", ["ltpc"] = new JsonArray(c.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) }).ToArray());
            }
            : null);
}

public sealed class HdfcSecuritiesFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_hdfcsecurities.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_hdfcsecurities.fixtures.json", name, () => new HdfcSecuritiesFeedCodec());
}

public sealed class HdfcSecuritiesFeedEndpointTests
{
    private static BrokerSession Session(string token = "tok en", string? key = "key&1") =>
        new(Guid.NewGuid(), "hdfcsecurities", token, null, null, key is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["ApiKey"] = key });

    [Fact]
    public void The_token_and_key_ride_in_the_address_and_the_token_again_in_the_header()
    {
        var plugin = new EPTradeBot.Infrastructure.Brokers.HdfcSecurities.HdfcSecuritiesPlugin(null!, null!, null!);
        var endpoint = plugin.FeedEndpoint(Session(), null)!;
        Assert.Equal("wss://developer.hdfcsec.com/wsapi/v1/session?token=tok%20en&api_key=key%261", endpoint.Url.AbsoluteUri);
        Assert.Equal("tok en", endpoint.Headers["Authorization"]);
        Assert.StartsWith("Mozilla/5.0", endpoint.Headers["User-Agent"]);
        Assert.Equal("{\"heart_beat\": true}", plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(10), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public void Without_a_token_or_a_key_there_is_no_endpoint()
    {
        var plugin = new EPTradeBot.Infrastructure.Brokers.HdfcSecurities.HdfcSecuritiesPlugin(null!, null!, null!);
        Assert.Null(plugin.FeedEndpoint(Session(token: ""), null));
        Assert.Null(plugin.FeedEndpoint(Session(key: null), null));
    }

    [Fact]
    public void A_test_address_replaces_the_host_and_path() =>
        Assert.StartsWith("ws://localhost:9/x?token=", new EPTradeBot.Infrastructure.Brokers.HdfcSecurities.HdfcSecuritiesPlugin(null!, null!, null!).FeedEndpoint(Session(), "ws://localhost:9/x")!.Url.AbsoluteUri);
}

public sealed class HdfcSkyFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_hdfcsky.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_hdfcsky.fixtures.json", name, () => new HdfcSkyFeedCodec());
}

public sealed class HdfcSkyFeedEndpointTests
{
    private static BrokerSession Session(string token = "tok en", string? key = "key&1") =>
        new(Guid.NewGuid(), "hdfcsky", token, null, null, key is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["ApiKey"] = key });

    private static EPTradeBot.Infrastructure.Brokers.HdfcSky.HdfcSkyPlugin Plugin() => new(null!, null!, null!, TimeProvider.System, null!);

    [Fact]
    public void The_token_and_key_ride_in_the_address_and_the_token_again_in_the_header()
    {
        var plugin = Plugin();
        var endpoint = plugin.FeedEndpoint(Session(), null)!;
        Assert.Equal("wss://developer.hdfcsky.com/wsapi/v1/session?token=tok%20en&api_key=key%261", endpoint.Url.AbsoluteUri);
        Assert.Equal("tok en", endpoint.Headers["Authorization"]);
        Assert.StartsWith("Mozilla/5.0", endpoint.Headers["User-Agent"]);
        Assert.Equal("{\"heart_beat\": true}", plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(10), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public void Without_a_token_or_a_key_there_is_no_endpoint()
    {
        Assert.Null(Plugin().FeedEndpoint(Session(token: ""), null));
        Assert.Null(Plugin().FeedEndpoint(Session(key: null), null));
    }
}

/// <summary>An MQTT feed codec against the Python adapter: the topics each step subscribes or drops and the ticks each message publishes.</summary>
public static class MqttFeedRunner
{
    public static void Run(string file, string name, Func<IMqttFeedCodec> codec)
    {
        var fixtures = BrokerContractHost.Load(file);
        var now = fixtures["now_ms"]!.GetValue<long>();
        var symbols = fixtures["symbols"]!.AsArray().OfType<JsonObject>().ToDictionary(s => (s["exchange"]!.GetValue<string>(), s["symbol"]!.GetValue<string>()), s => (Token: s["token"]!.GetValue<string>(), BrokerExchange: s["brexchange"]!.GetValue<string>(), BrokerSymbol: s["brsymbol"]!.GetValue<string>(), Name: s["name"]?.GetValue<string>()));
        var scenario = fixtures["scenarios"]!.AsArray().OfType<JsonObject>().Single(s => s["name"]!.GetValue<string>() == name);
        var feed = codec();
        var index = 0;
        foreach (var entry in scenario["steps"]!.AsArray().OfType<JsonObject>())
        {
            var step = entry["step"]!.AsObject();
            var result = entry["result"]!.AsObject();
            var kind = step.First().Key;
            var label = $"{name} step {++index} ({kind})";
            IReadOnlyList<MqttCommand> sent = [];
            IReadOnlyList<FeedPublish> published = [];
            switch (kind)
            {
                case "subscribe":
                    sent = feed.Subscribe(step["subscribe"]!.AsArray().OfType<JsonObject>().Select(i =>
                    {
                        var (token, brokerExchange, brokerSymbol, symbolName) = symbols[(i["exchange"]!.GetValue<string>(), i["symbol"]!.GetValue<string>())];
                        return new FeedRequest(i["exchange"]!.GetValue<string>(), i["symbol"]!.GetValue<string>(), token, brokerExchange, (StreamMode)i["mode"]!.GetValue<int>(), brokerSymbol, symbolName);
                    }).Where(r => (int)r.Mode is >= 1 and <= 3).ToList(), now);
                    break;
                case "unsubscribe":
                    var u = step["unsubscribe"]!.AsObject();
                    var (uToken, uBrokerExchange, uBrokerSymbol, uName) = symbols[(u["exchange"]!.GetValue<string>(), u["symbol"]!.GetValue<string>())];
                    sent = feed.Unsubscribe(new FeedTarget(u["exchange"]!.GetValue<string>(), u["symbol"]!.GetValue<string>(), uToken, uBrokerExchange, uBrokerSymbol, uName), u["mode"] is JsonValue m ? (StreamMode)m.GetValue<int>() : null, now);
                    break;
                case "reconnect":
                    sent = feed.Resubscribe(now);
                    break;
                case "message":
                    var message = step["message"]!.AsObject();
                    published = feed.DecodeMessage(message["topic"]!.GetValue<string>(), Convert.FromHexString(message["payload"]!.GetValue<string>()), now);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown step {kind}.");
            }

            var expectedSent = result["sent"]!.AsArray();
            Assert.True(expectedSent.Count == sent.Count, $"{label}: expected {expectedSent.Count} commands, got {sent.Count}");
            for (var i = 0; i < sent.Count; i++)
            {
                var expected = expectedSent[i]!.AsObject();
                Assert.Equal(sent[i].Subscribe ? "subscribe" : "unsubscribe", expected.First().Key);
                Assert.Equal(expected.First().Value!.AsArray().Select(x => x!.GetValue<string>()), sent[i].Topics);
            }

            var expectedPublished = result["published"]!.AsArray();
            Assert.True(expectedPublished.Count == published.Count, $"{label}: expected {expectedPublished.Count} ticks, got {published.Count}: {string.Join(", ", published.Select(p => p.Topic))}");
            for (var i = 0; i < published.Count; i++)
            {
                Assert.Equal(expectedPublished[i]!["topic"]!.GetValue<string>(), published[i].Topic);
                var diffs = JsonCompare.Differences(expectedPublished[i]!["data"], published[i].Data);
                Assert.True(diffs.Count == 0, $"{label}: tick {published[i].Topic} differs:\n{string.Join("\n", diffs)}");
            }
        }
    }
}

public sealed class IiflCapitalFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_iiflcapital.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => MqttFeedRunner.Run("feed_iiflcapital.fixtures.json", name, () => new IiflCapitalFeedCodec());
}

/// <summary>Answers the listing-id calls the real feed host would make for an index, so a recorded step that subscribes an index runs as the host runs it.</summary>
internal sealed class SamcoHostedCodec(SamcoFeedCodec inner) : IFeedCodec
{
    private static readonly Dictionary<string, string> Listings = new() { ["NIFTY"] = "-23", ["SENSEX"] = "-5" };

    public string Broker => inner.Broker;

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        var frames = inner.Subscribe(requests, nowMs).ToList();
        foreach (var call in inner.TakeHttp())
        {
            var symbol = call.Body["symbol"]!.GetValue<string>();
            inner.DecodeText(new JsonObject { ["samco_index"] = true, ["symbol"] = symbol, ["exchange"] = call.Body["exchange"]!.GetValue<string>(), ["listing_id"] = Listings[symbol] }.ToJsonString(), nowMs);
        }

        frames.AddRange(inner.TakeOutgoing());
        return frames;
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs) => inner.Unsubscribe(target, mode, nowMs);

    public IReadOnlyList<string> Resubscribe(long nowMs) => inner.Resubscribe(nowMs);

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => inner.DecodeBinary(frame, nowMs);

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => inner.DecodeText(frame, nowMs);

    public void Reset() => inner.Reset();

    public IReadOnlyList<string> TakeOutgoing() => inner.TakeOutgoing();
}

public sealed class SamcoFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_samco.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_samco.fixtures.json", name, () => new SamcoHostedCodec(new SamcoFeedCodec()),
        name == "reconnect_after_a_depth_unsubscribe"
            // OpenAlgo removes a depth subscription under a name that never matches the one it was stored under, so a depth symbol the trader unsubscribed comes back after a reconnect.
            // EP Trade bot forgets it, so the reconnect repeats only what is still held.
            ? scenario =>
            {
                var steps = scenario["steps"]!.AsArray();
                var sent = steps[2]!["result"]!["sent"]!.AsArray();
                sent.Clear();
                sent.Add(JsonNode.Parse("""{"request":{"streaming_type":"quote","data":{"symbols":[{"symbol":"1594_NSE"}]},"request_type":"subscribe","response_format":"json"}}"""));
            }
            : null);
}

public sealed class SamcoFeedEndpointTests
{
    private static BrokerSession Session(string token = "tok%20en") => new(Guid.NewGuid(), "samco", token, null, null, new Dictionary<string, string>());

    private static EPTradeBot.Infrastructure.Brokers.Samco.SamcoPlugin Plugin() => new(null!, null!, null!, TimeProvider.System);

    [Fact]
    public void The_session_token_rides_decoded_in_a_header()
    {
        var plugin = Plugin();
        var endpoint = plugin.FeedEndpoint(Session(), null)!;
        Assert.Equal("wss://stream.samco.in/", endpoint.Url.AbsoluteUri);
        Assert.Equal("tok en", endpoint.Headers["x-session-token"]);
        Assert.Null(plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public void Without_a_token_there_is_no_endpoint() => Assert.Null(Plugin().FeedEndpoint(Session(""), null));

    [Fact]
    public void An_index_listing_id_is_asked_for_once_and_streamed_under_itself()
    {
        var codec = new SamcoFeedCodec();
        var index = new FeedRequest("NSE_INDEX", "NIFTY", "NIFTY_50", "NSE", StreamMode.Quote, "NIFTY 50");
        Assert.Empty(codec.Subscribe([index], 1));
        var call = Assert.Single(codec.TakeHttp());
        Assert.Equal("/quote/indexQuote?indexName=NIFTY%2050", call.Path);

        // Asked again before the answer arrives, it is not asked a second time and nothing is sent yet.
        Assert.Empty(codec.Subscribe([index with { Mode = StreamMode.Ltp }], 2));
        Assert.Empty(codec.TakeHttp());

        codec.DecodeText("""{"samco_index":true,"symbol":"NIFTY","exchange":"NSE_INDEX","listing_id":"-23"}""", 3);
        var sent = codec.TakeOutgoing();
        Assert.Single(sent);
        Assert.Contains("\"symbol\":\"-23\"", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_samco_gives_no_listing_id_for_is_left_out_and_the_rest_still_goes()
    {
        var codec = new SamcoFeedCodec();
        Assert.Empty(codec.Subscribe([new FeedRequest("NSE_INDEX", "NIFTY", "NIFTY_50", "NSE", StreamMode.Quote, "NIFTY 50"), new FeedRequest("NSE", "SBIN", "3045_NSE", "NSE", StreamMode.Quote)], 1));
        codec.TakeHttp();
        codec.DecodeText("""{"samco_index":true,"symbol":"NIFTY","exchange":"NSE_INDEX","listing_id":null}""", 2);
        var sent = codec.TakeOutgoing();
        Assert.Single(sent);
        Assert.Contains("3045_NSE", sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("-23", sent[0], StringComparison.Ordinal);
    }
}

public sealed class FivePaisaFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_fivepaisa.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_fivepaisa.fixtures.json", name, () => new FivePaisaFeedCodec("4001234"));
}

public sealed class FivePaisaFeedEndpointTests
{
    private static string Jwt(string claims) => $"e30.{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.sig";

    private static BrokerSession Session(string token, string key = "app:::user:::4001234") =>
        new(Guid.NewGuid(), "fivepaisa", token, null, null, new Dictionary<string, string> { ["ApiKey"] = key });

    private static EPTradeBot.Infrastructure.Brokers.FivePaisa.FivePaisaPlugin Plugin() => new(null!, null!, null!);

    [Theory]
    [InlineData("""{"RedirectServer":"A"}""", "wss://aopenfeed.5paisa.com/feeds/api/chat")]
    [InlineData("""{"RedirectServer":"B"}""", "wss://bopenfeed.5paisa.com/feeds/api/chat")]
    [InlineData("""{"RedirectServer":"C"}""", "wss://openfeed.5paisa.com/feeds/api/chat")]
    [InlineData("""{"RedirectServer":"Z"}""", "wss://openfeed.5paisa.com/Feeds/api/chat")]
    [InlineData("""{"other":1}""", "wss://openfeed.5paisa.com/Feeds/api/chat")]
    public void The_host_is_the_one_the_token_names(string claims, string host)
    {
        var token = Jwt(claims);
        var endpoint = Plugin().FeedEndpoint(Session(token), null)!;
        Assert.Equal($"{host}?Value1={token}|4001234", endpoint.Url.OriginalString);
    }

    [Fact]
    public void A_token_that_is_not_a_jwt_takes_the_default_host() =>
        Assert.StartsWith("wss://openfeed.5paisa.com/Feeds/api/chat?Value1=plain|4001234", Plugin().FeedEndpoint(Session("plain"), null)!.Url.OriginalString, StringComparison.Ordinal);

    [Fact]
    public void Without_a_token_or_a_client_code_there_is_no_endpoint()
    {
        Assert.Null(Plugin().FeedEndpoint(Session(""), null));
        Assert.Null(Plugin().FeedEndpoint(Session("t", "app:::user"), null));
    }

    [Fact]
    public void The_codec_reads_the_client_code_from_the_session()
    {
        var codec = new FivePaisaFeedCodec();
        codec.Begin(Session("t"));
        var sent = codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 1);
        Assert.Contains("\"ClientCode\":\"4001234\"", Assert.Single(sent), StringComparison.Ordinal);
    }
}

public sealed class FirstockFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_firstock.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_firstock.fixtures.json", name, () => new FirstockFeedCodec());
}

public sealed class FirstockFeedEndpointTests
{
    private static BrokerSession Session(string token = "j-key", string key = "AB1234_API") =>
        new(Guid.NewGuid(), "firstock", token, null, null, new Dictionary<string, string> { ["ApiKey"] = key });

    private static EPTradeBot.Infrastructure.Brokers.Firstock.FirstockPlugin Plugin() => new(null!, null!, null!);

    [Fact]
    public void The_user_id_and_the_token_ride_in_the_query()
    {
        var plugin = Plugin();
        var endpoint = plugin.FeedEndpoint(Session(), null)!;
        Assert.Equal("wss://socket.firstock.in/V2/ws?userId=AB1234&jKey=j-key&source=developer-api", endpoint.Url.OriginalString);
        Assert.Empty(endpoint.Headers);
        Assert.Null(plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public void A_token_is_encoded_the_way_python_encodes_a_query()
    {
        var endpoint = Plugin().FeedEndpoint(Session("a b+c/d=e~f_g.h-i&j"), null)!;
        Assert.Contains("jKey=a+b%2Bc%2Fd%3De~f_g.h-i%26j&", endpoint.Url.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_id_loses_every_api_ending() =>
        Assert.Contains("userId=ABAPI1&", Plugin().FeedEndpoint(Session(key: "AB_APIAPI1_API"), null)!.Url.OriginalString, StringComparison.Ordinal);

    [Fact]
    public void An_address_can_be_overridden() =>
        Assert.StartsWith("wss://example.test/ws?userId=AB1234&", Plugin().FeedEndpoint(Session(), "wss://example.test/ws")!.Url.OriginalString, StringComparison.Ordinal);

    [Theory]
    [InlineData("", "AB1234_API")]
    [InlineData("  ", "AB1234_API")]
    [InlineData("tok", "")]
    [InlineData("tok", "_API")]
    public void Without_a_token_or_a_user_id_there_is_no_endpoint(string token, string key) => Assert.Null(Plugin().FeedEndpoint(Session(token, key), null));

    [Fact]
    public void Begin_makes_the_next_connection_wait_for_the_server_again()
    {
        var codec = new FirstockFeedCodec();
        var request = new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote);
        codec.DecodeText("""{"status":"success"}""", 1);
        Assert.Single(codec.Subscribe([request], 2));
        codec.Begin(Session());
        Assert.Empty(codec.Resubscribe(3));
        Assert.Empty(codec.TakeOutgoing());
        codec.DecodeText("""{"status":"success"}""", 4);
        Assert.Single(codec.TakeOutgoing());
    }

    [Fact]
    public void A_new_connection_is_tried_after_a_refused_sign_in()
    {
        var codec = new FirstockFeedCodec();
        var request = new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote);
        codec.Subscribe([request], 1);
        codec.DecodeText("""{"status":"failed"}""", 2);
        codec.DecodeText("""{"status":"success"}""", 3);
        Assert.Empty(codec.TakeOutgoing());
        codec.Begin(Session());
        codec.Resubscribe(4);
        codec.DecodeText("""{"status":"success"}""", 5);
        Assert.Equal("""{"action":"subscribe","tokens":"NSE:3045"}""", Assert.Single(codec.TakeOutgoing()));
    }

    [Fact]
    public void An_unsubscribe_with_no_mode_ends_every_mode()
    {
        var codec = new FirstockFeedCodec();
        codec.DecodeText("""{"status":"success"}""", 1);
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Ltp), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth)], 2);
        var sent = codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 3);
        Assert.Equal("""{"action":"unsubscribe","tokens":"NSE:3045"}""", Assert.Single(sent));
        Assert.Empty(codec.DecodeText("""{"c_symbol":"3045","c_exch_seg":"NSE","i_last_traded_price":80545}""", 4));
    }

    private static readonly FeedRequest Sbin = new("NSE", "SBIN", "3045", "NSE", StreamMode.Quote);

    [Fact]
    public void A_new_connection_waits_for_the_server_before_a_subscribe_goes()
    {
        var codec = new FirstockFeedCodec();
        codec.DecodeText("""{"status":"success"}""", 1);
        codec.Begin(Session());
        Assert.Empty(codec.Subscribe([Sbin], 2));
        codec.DecodeText("""{"status":"success"}""", 3);
        Assert.Single(codec.TakeOutgoing());
    }

    [Fact]
    public void Begin_alone_ends_a_refusal()
    {
        var codec = new FirstockFeedCodec();
        codec.Subscribe([Sbin], 1);
        codec.DecodeText("""{"status":"failed"}""", 2);
        codec.Begin(Session());
        codec.DecodeText("""{"status":"success"}""", 3);
        Assert.Single(codec.TakeOutgoing());
    }

    [Fact]
    public void Resubscribe_alone_ends_a_refusal()
    {
        var codec = new FirstockFeedCodec();
        codec.Subscribe([Sbin], 1);
        codec.DecodeText("""{"status":"failed"}""", 2);
        codec.Resubscribe(3);
        codec.DecodeText("""{"status":"success"}""", 4);
        Assert.Single(codec.TakeOutgoing());
    }

    [Fact]
    public void Reset_forgets_the_held_values_and_the_refusal()
    {
        var codec = new FirstockFeedCodec();
        codec.DecodeText("""{"status":"success"}""", 1);
        codec.Subscribe([Sbin], 2);
        codec.DecodeText("""{"c_symbol":"3045","c_exch_seg":"NSE","i_last_traded_price":80545}""", 3);
        codec.Reset();
        codec.DecodeText("""{"status":"success"}""", 4);
        codec.Subscribe([Sbin], 5);
        var tick = Assert.Single(codec.DecodeText("""{"c_symbol":"3045","c_exch_seg":"NSE","i_last_traded_price":0}""", 6));
        Assert.Equal(0.0, tick.Data["ltp"]!.GetValue<double>());

        codec.DecodeText("""{"status":"failed"}""", 7);
        codec.Reset();
        codec.DecodeText("""{"status":"success"}""", 8);
        Assert.Single(codec.Subscribe([Sbin], 9));
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var codec = new FirstockFeedCodec();
        codec.DecodeText("""{"status":"success"}""", 1);
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 2);
        codec.Reset();
        Assert.Empty(codec.DecodeText("""{"c_symbol":"3045","c_exch_seg":"NSE","i_last_traded_price":80545}""", 3));
        codec.DecodeText("""{"status":"success"}""", 4);
        Assert.Empty(codec.Subscribe([], 5));
        Assert.Empty(codec.TakeOutgoing());
    }
}

public sealed class KotakFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_kotak.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_kotak.fixtures.json", name, () => new KotakFeedCodec());
}

public sealed class KotakFeedEndpointTests
{
    private sealed class Config(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this, answer));

        private sealed class Handler(Config owner, Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Calls++;
                return Task.FromResult(answer(request));
            }
        }
    }

    private static BrokerSession Session(string token = "t:::sid:::https://cis.test:::access:::E43") => new(Guid.NewGuid(), "kotak", token, null, null, new Dictionary<string, string>());

    private static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static EPTradeBot.Infrastructure.Brokers.Kotak.KotakPlugin Plugin(IHttpClientFactory http) => new(http, null!, null!, TimeProvider.System);

    private const string Default = "wss://sfeed.kotaksecurities.com/apifeed";

    [Fact]
    public async Task The_data_centre_names_the_host()
    {
        var http = new Config(r => Json("""{"data":{"configs":{"E43_broadcast_source":"sh","E43_sh_broadcast_endpoint":"https://feed.test/apifeed"}}}"""));
        var endpoint = await Plugin(http).AuthorizeAsync(Session(), null, CancellationToken.None);
        Assert.Equal("wss://feed.test/apifeed", endpoint!.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"data":{"configs":{"E43_broadcast_source":"ks","E43_ks_broadcast_endpoint":"cdt.test/stream"}}}""", "wss://cdt.test/stream")]
    [InlineData("""{"data":{"configs":{"E43_broadcast_source":"sh","E43_sh_broadcast_endpoint":"http://feed.test/x"}}}""", "ws://feed.test/x")]
    [InlineData("""{"data":{"configs":{"E43_broadcast_source":"sh","E43_sh_broadcast_endpoint":"wss://feed.test/x"}}}""", "wss://feed.test/x")]
    [InlineData("""{"data":{"configs":{"E43_broadcast_source":"hs","E43_hs_broadcast_endpoint":"hs.test"}}}""", Default)]
    [InlineData("""{"data":{"configs":{"E43_broadcast_source":"sh"}}}""", Default)]
    [InlineData("""{"data":{"configs":{"E21_broadcast_source":"sh","E21_sh_broadcast_endpoint":"x.test"}}}""", Default)]
    [InlineData("""{"data":{}}""", Default)]
    [InlineData("""[1]""", Default)]
    [InlineData("not json", Default)]
    public async Task The_config_decides_the_host_and_anything_unusable_is_the_default(string body, string expected)
    {
        var endpoint = await Plugin(new Config(r => Json(body))).AuthorizeAsync(Session(), null, CancellationToken.None);
        Assert.Equal(expected, endpoint!.Url.AbsoluteUri);
    }

    [Fact]
    public async Task A_config_that_fails_is_the_default() =>
        Assert.Equal(Default, (await Plugin(new Config(r => Json("{}", System.Net.HttpStatusCode.InternalServerError))).AuthorizeAsync(Session(), null, CancellationToken.None))!.Url.AbsoluteUri);

    [Fact]
    public async Task A_session_with_no_data_centre_does_not_ask()
    {
        var http = new Config(r => Json("{}"));
        var endpoint = await Plugin(http).AuthorizeAsync(Session("t:::sid:::https://cis.test:::access"), null, CancellationToken.None);
        Assert.Equal(Default, endpoint!.Url.AbsoluteUri);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task An_override_is_used_without_asking()
    {
        var http = new Config(r => Json("{}"));
        Assert.Equal("wss://example.test/ws", (await Plugin(http).AuthorizeAsync(Session(), "wss://example.test/ws", CancellationToken.None))!.Url.AbsoluteUri);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task A_session_of_exactly_four_parts_has_a_feed()
    {
        Assert.NotNull(Plugin(new Config(r => Json("{}"))).FeedEndpoint(Session("t:::sid:::https://cis.test:::access"), null));
        Assert.Null(Plugin(new Config(r => Json("{}"))).FeedEndpoint(Session("t:::sid:::https://cis.test"), null));
        Assert.Null(await Plugin(new Config(r => Json("{}"))).AuthorizeAsync(Session("t:::sid:::https://cis.test"), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_that_is_not_a_kotak_session_has_no_feed()
    {
        Assert.Null(await Plugin(new Config(r => Json("{}"))).AuthorizeAsync(Session("plain"), null, CancellationToken.None));
        Assert.Null(Plugin(new Config(r => Json("{}"))).FeedEndpoint(Session("plain"), null));
    }

    [Fact]
    public void The_codec_signs_in_with_the_session_id_and_the_client_code()
    {
        var codec = new KotakFeedCodec();
        codec.Begin(new BrokerSession(Guid.NewGuid(), "kotak", "t:::the-sid:::https://cis.test:::access:::E43", null, null, new Dictionary<string, string> { ["ApiKey"] = "UCC123" }));
        var frame = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(codec.Resubscribe(5)))!;
        Assert.Equal("UCC123", frame["user"]!.GetValue<string>());
        Assert.Equal("the-sid", frame["auth"]!.GetValue<string>());
        Assert.Equal(5, frame["conn_req_time"]!.GetValue<long>());
    }

    [Fact]
    public void The_keep_alive_is_a_ping_every_thirty_seconds()
    {
        var plugin = Plugin(new Config(r => Json("{}")));
        Assert.Null(plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.FeedKeepAliveEvery);
    }
}

public sealed class DefinedgeFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_definedge.fixtures.json");

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_definedge.fixtures.json", name, () => new DefinedgeFeedCodec());
}

public sealed class DefinedgeFeedEndpointTests
{
    private static BrokerSession Session(string token = "key:::user-token:::api", string? feed = null, string? client = "U1") =>
        new(Guid.NewGuid(), "definedge", token, feed, client, new Dictionary<string, string>());

    private static EPTradeBot.Infrastructure.Brokers.Definedge.DefinedgePlugin Plugin() => new(null!, null!, null!, TimeProvider.System);

    [Fact]
    public void The_address_is_the_brokers()
    {
        var endpoint = Plugin().FeedEndpoint(Session(), null)!;
        Assert.Equal("wss://trade.definedgesecurities.com/NorenWSTRTP/", endpoint.Url.OriginalString);
        Assert.Empty(endpoint.Headers);
    }

    [Fact]
    public void A_configured_address_replaces_it() => Assert.Equal("ws://localhost:9/x", Plugin().FeedEndpoint(Session(), "ws://localhost:9/x")!.Url.OriginalString);

    [Theory]
    [InlineData("key:::user-token:::api", null, null)]
    [InlineData("key:::user-token:::api", null, " ")]
    [InlineData("key", null, "U1")]
    [InlineData("key:::", null, "U1")]
    public void Without_a_user_id_or_a_session_token_there_is_no_feed(string token, string? feed, string? client) => Assert.Null(Plugin().FeedEndpoint(Session(token, feed, client), null));

    [Fact]
    public void The_stored_feed_token_stands_in_for_the_second_part() => Assert.NotNull(Plugin().FeedEndpoint(Session("key", "stored-token"), null));

    [Fact]
    public void A_heartbeat_goes_every_thirty_seconds()
    {
        var plugin = Plugin();
        Assert.Equal("{\"t\":\"h\"}", plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public void The_connect_frame_names_the_user_twice_and_carries_the_session_token()
    {
        var codec = new DefinedgeFeedCodec();
        codec.Begin(Session());
        var frame = System.Text.Json.Nodes.JsonNode.Parse(codec.Resubscribe(0).Single())!;
        Assert.Equal("c", (string?)frame["t"]);
        Assert.Equal("U1", (string?)frame["uid"]);
        Assert.Equal("U1", (string?)frame["actid"]);
        Assert.Equal("TRTP", (string?)frame["source"]);
        Assert.Equal("user-token", (string?)frame["susertoken"]);
    }

    [Fact]
    public void The_stored_feed_token_is_what_the_connect_frame_carries()
    {
        var codec = new DefinedgeFeedCodec();
        codec.Begin(Session(feed: "stored-token"));
        Assert.Equal("stored-token", (string?)System.Text.Json.Nodes.JsonNode.Parse(codec.Resubscribe(0).Single())!["susertoken"]);
    }

    [Fact]
    public void A_connection_that_cannot_be_signed_in_sends_nothing()
    {
        var codec = new DefinedgeFeedCodec();
        codec.Begin(Session(client: null));
        Assert.Empty(codec.Resubscribe(0));
    }

    [Fact]
    public void The_codec_names_its_broker() => Assert.Equal("definedge", new DefinedgeFeedCodec().Broker);

    [Fact]
    public void A_session_that_is_only_a_key_has_no_token_to_sign_in_with()
    {
        var codec = new DefinedgeFeedCodec();
        codec.Begin(Session("key"));
        Assert.Empty(codec.Resubscribe(0));
    }

    [Fact]
    public void The_codec_is_a_new_one_for_each_connection() => Assert.NotSame(Plugin().CreateFeedCodec(), Plugin().CreateFeedCodec());
}

public sealed class AliceBlueFeedContractTests
{
    public static IEnumerable<object[]> Names() => FeedRunner.Names("feed_aliceblue.fixtures.json");

    /// <summary>A message of a type the feed does not define is published by OpenAlgo on a debug topic (DEBUG_MARKET_DATA) for a developer to read; EP Trade bot has no such topic and ignores it.</summary>
    private static readonly Dictionary<string, Action<JsonObject>> Adjusted = new()
    {
        ["an_unknown_message_type_is_ignored"] = scenario => scenario["steps"]![1]!["result"]!["published"] = new JsonArray(),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run("feed_aliceblue.fixtures.json", name, () => new AliceBlueFeedCodec(), Adjusted.GetValueOrDefault(name));
}

public sealed class AliceBlueFeedEndpointTests
{
    private static BrokerSession Session(string token = "ab-user-session", string? client = "AB1234") =>
        new(Guid.NewGuid(), "aliceblue", token, null, client, new Dictionary<string, string>());

    private sealed class Calls(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public List<(string Path, string Body, string? Authorization)> Seen { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, answer));

        private sealed class Handler(Calls owner, Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Seen.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()));
                return answer(request);
            }
        }
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static EPTradeBot.Infrastructure.Brokers.AliceBlue.AliceBluePlugin Plugin(IHttpClientFactory? http = null)
    {
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        return new(http!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), null!, TimeProvider.System);
    }

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    [Fact]
    public void The_address_is_the_first_server()
    {
        var endpoint = Plugin().FeedEndpoint(Session(), null)!;
        Assert.Equal("wss://ws1.aliceblueonline.com/NorenWS/", endpoint.Url.OriginalString);
        Assert.Empty(endpoint.Headers);
    }

    [Fact]
    public void A_configured_address_replaces_it() => Assert.Equal("ws://localhost:9/x", Plugin().FeedEndpoint(Session(), "ws://localhost:9/x")!.Url.OriginalString);

    [Theory]
    [InlineData("ab-user-session", null)]
    [InlineData("ab-user-session", " ")]
    [InlineData("", "AB1234")]
    public void Without_a_client_id_or_a_session_there_is_no_feed(string token, string? client) => Assert.Null(Plugin().FeedEndpoint(Session(token, client), null));

    [Fact]
    public void A_heartbeat_goes_every_thirty_seconds()
    {
        var plugin = Plugin();
        Assert.Equal("{\"k\":\"\",\"t\":\"h\"}", plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public async Task A_new_socket_session_is_asked_for_after_the_old_one_is_ended()
    {
        var http = new Calls(_ => Json("""{"status":"Ok"}"""));
        var endpoint = await Plugin(http).AuthorizeAsync(Session(), null, CancellationToken.None);
        Assert.NotNull(endpoint);
        Assert.Equal(["/open-api/od/v1/profile/invalidateWsSess", "/open-api/od/v1/profile/createWsSess"], http.Seen.Select(c => c.Path).ToArray());
        Assert.All(http.Seen, c => Assert.Equal("Bearer ab-user-session", c.Authorization));
        Assert.All(http.Seen, c =>
        {
            var body = JsonNode.Parse(c.Body)!;
            Assert.Equal("API", (string?)body["source"]);
            Assert.Equal("AB1234", (string?)body["userId"]);
        });
    }

    [Fact]
    public async Task An_old_session_that_cannot_be_ended_does_not_stop_a_new_one()
    {
        var http = new Calls(r => r.RequestUri!.AbsolutePath.EndsWith("invalidateWsSess", StringComparison.Ordinal) ? Json("""{"status":"Not_Ok"}""") : Json("""{"status":"Ok"}"""));
        Assert.NotNull(await Plugin(http).AuthorizeAsync(Session(), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"status":"Not_Ok"}""")]
    [InlineData("""{"emsg":"x"}""")]
    [InlineData("[1]")]
    public async Task A_socket_session_that_is_refused_means_no_feed(string answer)
    {
        var http = new Calls(r => r.RequestUri!.AbsolutePath.EndsWith("createWsSess", StringComparison.Ordinal) ? Json(answer) : Json("""{"status":"Ok"}"""));
        Assert.Null(await Plugin(http).AuthorizeAsync(Session(), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_socket_session_that_cannot_be_reached_means_no_feed()
    {
        var http = new Calls(_ => throw new HttpRequestException("down"));
        Assert.Null(await Plugin(http).AuthorizeAsync(Session(), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_configured_address_is_used_without_asking_for_a_session()
    {
        var http = new Calls(_ => Json("""{"status":"Ok"}"""));
        Assert.NotNull(await Plugin(http).AuthorizeAsync(Session(), "ws://localhost:9/x", CancellationToken.None));
        Assert.Empty(http.Seen);
    }

    [Fact]
    public void The_connect_frame_hashes_the_session_twice_and_names_the_user_with_the_api_suffix()
    {
        var codec = new AliceBlueFeedCodec();
        codec.Begin(Session());
        var frame = JsonNode.Parse(codec.Resubscribe(0).Single())!;
        var inner = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ab-user-session")));
        var outer = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inner)));
        Assert.Equal("c", (string?)frame["t"]);
        Assert.Equal("AB1234_API", (string?)frame["uid"]);
        Assert.Equal("AB1234_API", (string?)frame["actid"]);
        Assert.Equal("API", (string?)frame["source"]);
        Assert.Equal(outer, (string?)frame["susertoken"]);
    }

    [Fact]
    public void A_session_with_no_token_sends_no_connect_frame()
    {
        var codec = new AliceBlueFeedCodec();
        codec.Begin(Session(""));
        Assert.Empty(codec.Resubscribe(0));
    }

    [Fact]
    public void The_codec_names_its_broker() => Assert.Equal("aliceblue", new AliceBlueFeedCodec().Broker);

    [Fact]
    public void The_codec_is_a_new_one_for_each_connection() => Assert.NotSame(Plugin().CreateFeedCodec(), Plugin().CreateFeedCodec());
}
