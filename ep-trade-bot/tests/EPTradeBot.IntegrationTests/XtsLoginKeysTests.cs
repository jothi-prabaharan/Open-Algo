using EPTradeBot.Core.Brokers;
using EPTradeBot.Infrastructure.Brokers.Xts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>An XTS sign-in needs all four keys and says so before anything is sent to the broker.</summary>
public sealed class XtsLoginKeysTests
{
    [PostgresTheory]
    [InlineData("ApiKey")]
    [InlineData("ApiSecret")]
    [InlineData("ApiKeyMarket")]
    [InlineData("ApiSecretMarket")]
    public async Task A_missing_key_is_refused_before_anything_is_sent(string missing)
    {
        var broker = new FakeBroker();
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "wisdom");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var keys = new Dictionary<string, string> { ["ApiKey"] = "a", ["ApiSecret"] = "b", ["ApiKeyMarket"] = "c", ["ApiSecretMarket"] = "d" };
        keys[missing] = "  ";

        var result = await host.Get<WisdomPlugin>().LoginAsync(keys, new Dictionary<string, string>(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(409, result.HttpStatus);
        Assert.Contains("not all set", result.Message);
        Assert.Empty(broker.Sent);
    }
}
