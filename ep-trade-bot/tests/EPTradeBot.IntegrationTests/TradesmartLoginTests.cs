using EPTradeBot.Core.Brokers;
using EPTradeBot.Infrastructure.Brokers.Shoonya;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>TradeSmart's sign-in address, the user id it keeps in front of the token, and the way that token is shown to the broker.</summary>
public sealed class TradesmartLoginTests
{
    private static TestHost NewHost(FakeBroker? broker = null) => new("Production", b =>
    {
        b.UseSetting("Brokers:Enabled:0", "tradesmart");
        if (broker is not null) b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
    });

    [PostgresFact]
    public async Task The_sign_in_address_names_the_client_id_after_the_user_id()
    {
        await using var host = NewHost();
        var url = host.Get<TradesmartPlugin>().LoginUrl(new Dictionary<string, string> { ["ApiKey"] = "FA12345:::client id" }, "state");
        Assert.Equal("https://v2api.tradesmartonline.in/OAuthlogin/authorize/oauth?client_id=client%20id", url);
    }

    [PostgresFact]
    public async Task The_user_id_the_broker_sends_is_kept_in_front_of_the_token()
    {
        var broker = new FakeBroker();
        broker.Reset(new Dictionary<string, (int, string)> { ["POST /NorenWClientAPIv2/GenAcsTok"] = (200, """{"stat":"Ok","access_token":"tok-1","actid":"TS0001"}""") });
        await using var host = NewHost(broker);
        var result = await host.Get<TradesmartPlugin>().LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "FA12345:::cid", ["ApiSecret"] = "s" }, new Dictionary<string, string> { ["code"] = "c" }, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("TS0001:::tok-1", result.AuthToken);
    }

    [PostgresFact]
    public async Task A_call_names_the_user_from_the_token_and_shows_the_broker_only_the_token()
    {
        var broker = new FakeBroker();
        await using var host = NewHost(broker);
        broker.Reset(new Dictionary<string, (int, string)> { ["POST /NorenWClientAPIv2/Limits"] = (200, """{"stat":"Ok","cash":"1","payin":"0","marginused":"0","brkcollamt":"0"}"""), ["POST /NorenWClientAPIv2/PositionBook"] = (200, """{"stat":"Not_Ok","emsg":"no data"}""") });
        var session = new BrokerSession(Guid.NewGuid(), "tradesmart", "TS0001:::tok-1", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA12345:::cid" });

        await host.Get<TradesmartPlugin>().GetFundsAsync(session, CancellationToken.None);

        var first = broker.Sent[0];
        Assert.Equal("Bearer tok-1", first.Headers["Authorization"]);
        Assert.Equal("TS0001", first.Json!["uid"]!.GetValue<string>());
    }
}
