using System.Net;
using EPTradeBot.Infrastructure.Flow;

namespace EPTradeBot.IntegrationTests;

/// <summary>The address check behind the HTTP request node: only a public address may be called.</summary>
public sealed class FlowHttpTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Private_loopback_link_local_reserved_and_multicast_addresses_are_not_public(string address) => Assert.False(FlowHttp.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("2606:4700:4700::1111")]
    public void Ordinary_public_addresses_are_allowed(string address) => Assert.True(FlowHttp.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://127.0.0.1:5000/api", "not a public address")]
    [InlineData("http://localhost/x", "not a public address")]
    [InlineData("http://[::1]/", "not a public address")]
    [InlineData("http://169.254.169.254/latest/meta-data", "not a public address")]
    [InlineData("ftp://8.8.8.8/file", "scheme 'ftp' is not allowed")]
    [InlineData("file:///etc/passwd", "scheme 'file' is not allowed")]
    [InlineData("not a url", "URL could not be parsed")]
    public async Task A_destination_that_is_not_public_or_not_http_is_refused_with_the_reason(string url, string words)
    {
        var reason = await new FlowHttp().CheckDestinationAsync(url, default);
        Assert.NotNull(reason);
        Assert.Contains(words, reason);
    }

    [Fact]
    public async Task A_public_address_literal_passes_without_any_lookup() => Assert.Null(await new FlowHttp().CheckDestinationAsync("https://8.8.8.8/dns-query", default));
}
