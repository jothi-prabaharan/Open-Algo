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
        var node = JsonNode.Parse(message);
        if (node is JsonObject o && o["guid"] is not null) o["guid"] = "<guid>";
        return node!.ToJsonString();
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
        if (fixtures["access_token"] is JsonValue token) feed.Begin(new BrokerSession(Guid.Empty, fixtures["broker"]!.GetValue<string>(), token.GetValue<string>(), null, null, new Dictionary<string, string>()));
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
                sent = sent.Select(x => Stand(x)).OrderBy(x => JsonNode.Parse(x)!.ToJsonString(), StringComparer.Ordinal).ToList();
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
