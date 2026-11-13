using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Entities.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>Upstox hands out the market-data socket address for the login token; a connection asks for it first.</summary>
public sealed class UpstoxFeedAuthorizeTests
{
    private static async Task<(FeedEndpoint? Endpoint, FakeBroker Broker)> AskAsync(int status, string body, string? overrideUrl = null)
    {
        var broker = new FakeBroker();
        broker.Reset(new Dictionary<string, (int, string)> { ["GET /v3/feed/market-data-feed/authorize"] = (status, body) });
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "upstox");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var plugin = host.Get<IBrokerRegistry>().Get<IBrokerFeedAuthorize>("upstox")!;
        var session = new BrokerSession(Guid.NewGuid(), "upstox", "upstox-token", null, null, new Dictionary<string, string>());
        return (await plugin.AuthorizeAsync(session, overrideUrl, CancellationToken.None), broker);
    }

    [PostgresFact]
    public async Task The_address_Upstox_hands_out_is_the_one_connected_to_and_the_token_goes_with_the_request()
    {
        var (endpoint, broker) = await AskAsync(200, """{"status":"success","data":{"authorized_redirect_uri":"wss://wsfeeder-api.upstox.com/market-data-feeder/v3/abc?code=xyz"}}""");
        Assert.Equal("wss://wsfeeder-api.upstox.com/market-data-feeder/v3/abc?code=xyz", endpoint!.Url.ToString());
        Assert.Equal("Bearer upstox-token", Assert.Single(broker.Sent).Headers["Authorization"]);
    }

    [PostgresFact]
    public async Task A_refused_login_gives_no_address()
    {
        Assert.Null((await AskAsync(401, """{"status":"error","errors":[{"errorCode":"UDAPI100050","message":"Invalid token"}]}""")).Endpoint);
    }

    [PostgresFact]
    public async Task An_answer_without_an_address_gives_none()
    {
        Assert.Null((await AskAsync(200, """{"status":"success","data":{}}""")).Endpoint);
        Assert.Null((await AskAsync(429, """{"status":"error"}""")).Endpoint);
    }

    [PostgresFact]
    public async Task A_configured_address_is_used_without_asking_Upstox()
    {
        var (endpoint, broker) = await AskAsync(500, "{}", "ws://127.0.0.1:9/feed");
        Assert.Equal("ws://127.0.0.1:9/feed", endpoint!.Url.ToString());
        Assert.Empty(broker.Sent);
    }
}
