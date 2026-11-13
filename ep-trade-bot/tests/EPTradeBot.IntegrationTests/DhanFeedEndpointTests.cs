using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using Microsoft.AspNetCore.Hosting;

namespace EPTradeBot.IntegrationTests;

/// <summary>Dhan's 5-level socket takes the access token and the client id on its address.</summary>
public sealed class DhanFeedEndpointTests
{
    private static async Task<FeedEndpoint?> AskAsync(string? apiKey, string? accountClient, string token = "dhan-token", string? overrideUrl = null)
    {
        await using var host = new TestHost("Production", b => b.UseSetting("Brokers:Enabled:0", "dhan"));
        var plugin = (IBrokerFeed)host.Get<IBrokerRegistry>().Get<IBrokerFeed>("dhan")!;
        var keys = apiKey is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["ApiKey"] = apiKey };
        return plugin.FeedEndpoint(new BrokerSession(Guid.NewGuid(), "dhan", token, null, accountClient, keys), overrideUrl);
    }

    [PostgresFact]
    public async Task The_client_id_saved_with_the_key_and_the_token_ride_on_the_address()
    {
        var endpoint = await AskAsync("KEYCLIENT:::app-key", "ACCOUNT", "a b+c");
        Assert.Equal("wss://api-feed.dhan.co/?version=2&token=a%20b%2Bc&clientId=KEYCLIENT&authType=2", endpoint!.Url.AbsoluteUri);
    }

    [PostgresFact]
    public async Task Without_a_client_id_in_the_key_the_accounts_is_used()
    {
        Assert.Contains("clientId=ACCOUNT&", (await AskAsync("plain-key", "ACCOUNT"))!.Url.ToString());
    }

    [PostgresFact]
    public async Task No_client_id_or_no_token_means_no_feed()
    {
        Assert.Null(await AskAsync("plain-key", null));
        Assert.Null(await AskAsync("KEYCLIENT:::k", null, token: ""));
    }

    [PostgresFact]
    public async Task A_configured_address_replaces_the_default_one()
    {
        Assert.StartsWith("ws://127.0.0.1:9/feed?version=2", (await AskAsync("K:::k", null, overrideUrl: "ws://127.0.0.1:9/feed"))!.Url.ToString());
    }
}
