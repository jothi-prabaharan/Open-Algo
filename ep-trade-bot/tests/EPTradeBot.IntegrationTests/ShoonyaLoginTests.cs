using EPTradeBot.Infrastructure.Brokers.Shoonya;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>Shoonya's sign-in address and the keys it needs.</summary>
public sealed class ShoonyaLoginTests
{
    private static TestHost NewHost(FakeBroker? broker = null) => new("Production", b =>
    {
        b.UseSetting("Brokers:Enabled:0", "shoonya");
        if (broker is not null) b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
    });

    [PostgresFact]
    public async Task The_sign_in_address_names_the_client_id_after_the_user_id()
    {
        await using var host = NewHost();
        var url = host.Get<ShoonyaPlugin>().LoginUrl(new Dictionary<string, string> { ["ApiKey"] = "FA12345:::client id" }, "state");
        Assert.Equal("https://api.shoonya.com/OAuthlogin/authorize/oauth?client_id=client%20id", url);
    }

    [PostgresTheory]
    [InlineData("FA12345")]
    [InlineData("FA12345:::")]
    [InlineData("")]
    public async Task A_key_without_a_client_id_has_no_address_and_refuses_a_sign_in(string key)
    {
        await using var host = NewHost();
        var plugin = host.Get<ShoonyaPlugin>();
        var keys = new Dictionary<string, string> { ["ApiKey"] = key, ["ApiSecret"] = "s" };
        Assert.Null(plugin.LoginUrl(keys, "state"));
        var result = await plugin.LoginAsync(keys, new Dictionary<string, string> { ["code"] = "c" }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(409, result.HttpStatus);
    }

    [PostgresFact]
    public async Task A_sign_in_without_the_secret_or_the_code_is_refused_before_anything_is_sent()
    {
        var broker = new FakeBroker();
        await using var host = NewHost(broker);
        var plugin = host.Get<ShoonyaPlugin>();
        var noSecret = await plugin.LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "U:::C" }, new Dictionary<string, string> { ["code"] = "c" }, CancellationToken.None);
        Assert.Equal(409, noSecret.HttpStatus);
        var noCode = await plugin.LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "U:::C", ["ApiSecret"] = "s" }, new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(400, noCode.HttpStatus);
        var blankSecret = await plugin.LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "U:::C", ["ApiSecret"] = "  " }, new Dictionary<string, string> { ["code"] = "c" }, CancellationToken.None);
        Assert.Equal(409, blankSecret.HttpStatus);
        var blankCode = await plugin.LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "U:::C", ["ApiSecret"] = "s" }, new Dictionary<string, string> { ["code"] = " " }, CancellationToken.None);
        Assert.Equal(400, blankCode.HttpStatus);
        Assert.Empty(broker.Sent);
    }
}

/// <summary>Shoonya's chart calls carry the token in the form body, beside the JSON.</summary>
public sealed class ShoonyaChartTokenTests
{
    [PostgresFact]
    public async Task A_chart_call_sends_the_json_and_the_token_in_one_form()
    {
        var broker = new FakeBroker();
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "shoonya");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        broker.Reset(new Dictionary<string, (int, string)> { ["POST /NorenWClientAPI/EODChartData"] = (200, "[]") });
        var session = new EPTradeBot.Core.Brokers.BrokerSession(Guid.NewGuid(), "shoonya", "tok-123", null, null, new Dictionary<string, string> { ["ApiKey"] = "FA1:::c" });
        var record = new EPTradeBot.Core.Brokers.SymbolRecord("SBIN", "SBIN-EQ", "NSE", "NSE", "3045", null, null, null, 1, "EQ", 0.05);

        await host.Get<ShoonyaPlugin>().GetHistoryAsync(session, record, "D", new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30), CancellationToken.None);

        var sent = Assert.Single(broker.Sent);
        Assert.StartsWith("jData=", sent.RawBody);
        Assert.EndsWith("&jKey=tok-123", sent.RawBody);
        Assert.StartsWith("application/x-www-form-urlencoded", sent.Headers["Content-Type"]);
    }
}
