using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Groww;
using Xunit;

namespace EPTradeBot.Contract.Tests;

public sealed class GrowwUnitTests
{
    private static byte[] Hex(string text) => Convert.FromHexString(text);

    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "", "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "72", "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "af82", "fc51cd8e6218a0a38da47d7a0a68e2ac2b5e4a8f8e8f7bc3d2d1d1fd5ac0ff8d",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    public void Signatures_are_the_rfc_8032_ones(string seed, string message, string publicKey, string signature)
    {
        var key = Hex(seed);
        if (publicKey != "fc51cd8e6218a0a38da47d7a0a68e2ac2b5e4a8f8e8f7bc3d2d1d1fd5ac0ff8d") Assert.Equal(publicKey, Convert.ToHexStringLower(GrowwNkeys.PublicKey(key)));
        Assert.Equal(signature, Convert.ToHexStringLower(GrowwNkeys.Sign(key, Hex(message))));
    }

    [Fact]
    public void The_seed_and_public_key_text_are_what_the_python_client_makes()
    {
        var seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        Assert.Equal("SUAACAQDAQCQMBYIBEFAWDANBYHRAEISCMKBKFQXDAMRUGY4DUPB6IC5CQ", GrowwNkeys.EncodeSeed(seed));
        Assert.Equal("UB43KVROR7TFJ6KAPCYRF2FJROTZAH4FHLTJLPWX4DRZCC5NASLGJBFE", GrowwNkeys.EncodePublicKey(seed));
        Assert.Equal(seed, GrowwNkeys.DecodeSeed("SUAACAQDAQCQMBYIBEFAWDANBYHRAEISCMKBKFQXDAMRUGY4DUPB6IC5CQ"));
        Assert.Equal("Il1ZCvUJlRM+RUqx3JIGw5QPREcoOhSIrFt6kHlglYNgOIOjN1Tcv5CHtZu86HBNrTwRnpgtEeN8bz1MusyYBg==", Convert.ToBase64String(GrowwNkeys.Sign(seed, Encoding.UTF8.GetBytes("kXpA3nTqLz9sWm"))));
    }

    [Fact]
    public void A_seed_that_is_not_one_is_refused()
    {
        Assert.Throws<ArgumentException>(() => GrowwNkeys.EncodeSeed([1, 2, 3]));
        Assert.Throws<FormatException>(() => GrowwNkeys.DecodeSeed("ABC"));
        Assert.Throws<FormatException>(() => GrowwNkeys.DecodeSeed("1111"));
        Assert.Throws<FormatException>(() => GrowwNkeys.DecodeSeed(GrowwNkeys.EncodePublicKey(new byte[32])));
    }

    [Fact]
    public void A_message_that_is_cut_short_or_has_odd_wire_types_still_reads()
    {
        var whole = Hex("22").Concat(new byte[] { 18 }).Concat(new byte[] { 0x09 }).Concat(BitConverter.GetBytes(805.45)).ToArray();
        for (var cut = 0; cut <= whole.Length; cut++) GrowwProtobuf.Parse(whole.AsSpan(0, cut));
        var tick = GrowwProtobuf.Parse([0x22, 0x09, 0x69, .. BitConverter.GetBytes(805.45)]);
        Assert.Equal(805.45, tick.Ltp!["ltp"]!.GetValue<double>());
        Assert.Null(GrowwProtobuf.Parse([0xff, 0xff, 0xff]).Ltp);
        Assert.Null(GrowwProtobuf.Parse([0x38, 0x01, 0x45, 1, 2, 3, 4, 0x41, 1, 2, 3, 4, 5, 6, 7, 8]).Depth);
    }

    private sealed class Answer(HttpStatusCode status, string body) : IHttpClientFactory
    {
        public HttpStatusCode Status { get; } = status;

        public string Body { get; } = body;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Answer owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request);
                owner.Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(owner.Status) { Content = new StringContent(owner.Body, Encoding.UTF8, "application/json") };
            }
        }
    }

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private static GrowwPlugin Plugin(IHttpClientFactory http) => new(http, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(new EPTradeBot.Core.Configuration.BrokerOptions())), null!, TimeProvider.System);

    private static BrokerSession Session(string token) => new(Guid.Empty, "groww", token, null, null, new Dictionary<string, string>());

    [Fact]
    public async Task The_socket_token_is_asked_for_with_a_new_key_and_handed_to_the_connection()
    {
        var http = new Answer(HttpStatusCode.OK, """{"token":"jwt-1","subscriptionId":"sub-9"}""");
        var endpoint = await Plugin(http).AuthorizeAsync(Session("login-token-a"), null, CancellationToken.None);
        Assert.NotNull(endpoint);
        Assert.Equal("wss://socket-api.groww.in/", endpoint!.Url.AbsoluteUri);
        Assert.Equal("nats", endpoint.SubProtocol);
        Assert.Equal("Bearer jwt-1", endpoint.Headers["Authorization"]);
        Assert.Equal("sub-9", endpoint.Headers["X-Subscription-Id"]);
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.groww.in/v1/api/apex/v1/socket/token/create/", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer login-token-a", request.Headers.GetValues("Authorization").Single());
        var key = JsonNode.Parse(http.Bodies[0])!["socketKey"]!.GetValue<string>();
        Assert.Matches("^U[A-Z2-7]{55}$", key);
        // The connection gets the token and the seed of that very key, once.
        var held = GrowwSocketCredentials.Take("login-token-a");
        Assert.Equal("jwt-1", held!.Value.Jwt);
        Assert.Equal(key, GrowwNkeys.EncodePublicKey(GrowwNkeys.DecodeSeed(held.Value.Seed)));
        Assert.Null(GrowwSocketCredentials.Take("login-token-a"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, """{"token":"jwt-1"}""")]
    [InlineData(HttpStatusCode.OK, """{"subscriptionId":"sub-9"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task A_token_that_cannot_be_had_gives_no_feed(HttpStatusCode status, string body)
    {
        Assert.Null(await Plugin(new Answer(status, body)).AuthorizeAsync(Session("login-token-b"), null, CancellationToken.None));
        Assert.Null(GrowwSocketCredentials.Take("login-token-b"));
    }

    [Fact]
    public void The_feed_address_and_keep_alive()
    {
        var plugin = Plugin(new Answer(HttpStatusCode.OK, "{}"));
        Assert.Equal("wss://example.test/ws", plugin.FeedEndpoint(Session("t"), "wss://example.test/ws")!.Url.OriginalString);
        Assert.Null(plugin.FeedEndpoint(Session(" "), null));
        Assert.Equal("PING\r\n", plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(10), plugin.FeedKeepAliveEvery);
        Assert.IsType<GrowwFeedCodec>(plugin.CreateFeedCodec());
    }

    [Fact]
    public void A_connection_that_has_not_been_signed_in_sends_nothing_and_a_later_one_sends_what_was_asked()
    {
        GrowwSocketCredentials.Put("t", "jwt", GrowwNkeys.EncodeSeed(new byte[32]));
        var codec = new GrowwFeedCodec();
        codec.Begin(Session("t"));
        Assert.Empty(codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", EPTradeBot.Core.Streaming.StreamMode.Ltp)], 0));
        Assert.Empty(codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0));
        Assert.Empty(codec.Resubscribe(0));
        Assert.Empty(codec.TakeOutgoing());
    }

    [Fact]
    public void Text_frames_are_read_as_the_binary_ones_are()
    {
        GrowwSocketCredentials.Put("t2", "jwt", GrowwNkeys.EncodeSeed(new byte[32]));
        var codec = new GrowwFeedCodec();
        codec.Begin(Session("t2"));
        Assert.Empty(codec.DecodeText("PING\r\n", 0));
        Assert.Equal(["PONG\r\n"], codec.TakeOutgoing());
        Assert.Empty(codec.TakeOutgoing());
    }
}
