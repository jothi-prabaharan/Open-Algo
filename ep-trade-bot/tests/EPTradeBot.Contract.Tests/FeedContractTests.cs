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
