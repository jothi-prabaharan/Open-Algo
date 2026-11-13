using EPTradeBot.Infrastructure.Brokers.Xts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>CompositEdge's sign-in address, and the session its page sends back.</summary>
public sealed class CompositEdgeLoginTests
{
    private static readonly Dictionary<string, string> Keys = new()
    {
        ["ApiKey"] = "ce app+key", ["ApiSecret"] = "s", ["ApiKeyMarket"] = "mk", ["ApiSecretMarket"] = "ms", ["RedirectUrl"] = "http://127.0.0.1:5000/compositedge/callback",
    };

    private static TestHost NewHost(FakeBroker broker) => new("Production", b =>
    {
        b.UseSetting("Brokers:Enabled:0", "compositedge");
        b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
    });

    [PostgresFact]
    public async Task The_sign_in_address_carries_the_key_and_the_state_on_the_return_address()
    {
        await using var host = NewHost(new FakeBroker());
        var url = host.Get<CompositEdgePlugin>().LoginUrl(Keys, "abc123");
        Assert.Equal("https://xts.compositedge.com/interactive/thirdparty?appKey=ce%20app%2Bkey&returnURL=http%3A%2F%2F127.0.0.1%3A5000%2Fcompositedge%2Fcallback%3Fstate%3Dabc123", url);
    }

    [PostgresTheory]
    [InlineData("ApiKey")]
    [InlineData("RedirectUrl")]
    public async Task Without_the_key_or_the_return_address_there_is_no_address(string missing)
    {
        await using var host = NewHost(new FakeBroker());
        var keys = new Dictionary<string, string>(Keys) { [missing] = " " };
        Assert.Null(host.Get<CompositEdgePlugin>().LoginUrl(keys, "abc123"));
    }

    [PostgresTheory]
    [InlineData("""{"accessToken":"tok-1","userID":"u"}""")]
    [InlineData("\"{\\\"accessToken\\\":\\\"tok-1\\\"}\"")]
    [InlineData("tok-1")]
    public async Task The_access_token_is_found_in_the_session_the_page_sends_back_or_taken_as_given(string given)
    {
        var broker = new FakeBroker();
        await using var host = NewHost(broker);
        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["POST /interactive/user/session"] = (200, """{"type":"success","result":{"token":"s","userID":"A"}}"""),
            ["POST /apimarketdata/auth/login"] = (200, """{"type":"success","result":{"token":"f","userID":"A"}}"""),
        });

        var result = await host.Get<CompositEdgePlugin>().LoginAsync(Keys, new Dictionary<string, string> { ["request_token"] = given }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var sent = broker.Sent.First(s => s.Path == "/interactive/user/session");
        Assert.Equal("tok-1", sent.Json!["accessToken"]!.GetValue<string>());
    }
}
