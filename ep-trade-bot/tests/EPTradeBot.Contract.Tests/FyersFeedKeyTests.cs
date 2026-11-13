using System.Text;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Contract.Tests;

/// <summary>The key that signs the Fyers socket in comes out of the access token; an expired or malformed token gives none.</summary>
public sealed class FyersFeedKeyTests
{
    private static string Token(string payloadJson, string? prefix = null)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{prefix}e30.{payload}.sig";
    }

    [Fact]
    public void The_key_is_read_from_a_bare_token_and_from_one_with_the_app_id_in_front()
    {
        var json = """{"hsm_key":"abc123","exp":4000000000}""";
        Assert.Equal("abc123", FyersFeedCodec.ExtractHsmKey(Token(json)));
        Assert.Equal("abc123", FyersFeedCodec.ExtractHsmKey(Token(json, "APP-100:")));
    }

    [Fact]
    public void An_expired_token_gives_no_key()
    {
        Assert.Null(FyersFeedCodec.ExtractHsmKey(Token("""{"hsm_key":"abc123","exp":1}""")));
    }

    [Fact]
    public void A_token_without_a_key_or_not_a_token_gives_none()
    {
        Assert.Null(FyersFeedCodec.ExtractHsmKey(Token("""{"exp":4000000000}""")));
        Assert.Null(FyersFeedCodec.ExtractHsmKey("not-a-token"));
        Assert.Null(FyersFeedCodec.ExtractHsmKey("a.!!!.c"));
        Assert.Null(FyersFeedCodec.ExtractHsmKey(""));
    }

    [Fact]
    public void Without_a_key_the_codec_signs_in_with_nothing_and_so_subscribes_to_nothing()
    {
        var codec = new FyersFeedCodec();
        codec.Begin(new EPTradeBot.Core.Brokers.BrokerSession(Guid.Empty, "fyers", "garbage", null, null, new Dictionary<string, string>()));
        Assert.Empty(codec.Resubscribe(0));
    }
}
