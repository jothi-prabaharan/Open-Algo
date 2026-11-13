using EPTradeBot.Infrastructure.Brokers.Shoonya;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>Flattrade's sign-in address and the keys it needs.</summary>
public sealed class FlattradeLoginTests
{
    private static TestHost NewHost() => new("Production", b => b.UseSetting("Brokers:Enabled:0", "flattrade"));

    [PostgresFact]
    public async Task The_sign_in_address_names_the_app_key_after_the_user_id()
    {
        await using var host = NewHost();
        Assert.Equal("https://auth.flattrade.in/?app_key=app%20key", host.Get<FlattradePlugin>().LoginUrl(new Dictionary<string, string> { ["ApiKey"] = "FA1:::app key" }, "state"));
    }

    [PostgresTheory]
    [InlineData("FA1")]
    [InlineData("FA1:::")]
    public async Task A_key_without_an_app_key_has_no_address(string key)
    {
        await using var host = NewHost();
        Assert.Null(host.Get<FlattradePlugin>().LoginUrl(new Dictionary<string, string> { ["ApiKey"] = key }, "state"));
    }
}
