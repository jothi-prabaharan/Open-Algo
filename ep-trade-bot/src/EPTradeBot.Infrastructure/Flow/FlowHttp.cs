using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;

namespace EPTradeBot.Infrastructure.Flow;

/// <summary>
/// Outbound requests for the HTTP request node. It has its own client with no redirects (so a redirect cannot escape the check below) and no cookies, apart from the shared broker pool, and refuses
/// any destination that is not a public address: this machine, the local network, link-local (cloud metadata at 169.254.169.254), reserved, multicast and unspecified ranges.
/// </summary>
public sealed class FlowHttp : IFlowHttp
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<string?> CheckDestinationAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "URL could not be parsed";
        if (uri.Scheme is not ("http" or "https")) return $"scheme '{uri.Scheme}' is not allowed; use http or https";
        if (string.IsNullOrEmpty(uri.Host)) return "URL has no host";
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException e)
        {
            return $"host '{uri.Host}' could not be resolved ({e.Message})";
        }

        foreach (var address in addresses)
            if (!IsPublic(address)) return $"host '{uri.Host}' resolves to {address}, which is not a public address";
        return null;
    }

    /// <summary>True only for a globally routable address.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && b[1] is 18 or 19)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
    }

    public async Task<FlowHttpResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, JsonNode? jsonBody, string? textBody, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (jsonBody is not null) request.Content = new StringContent(jsonBody.ToJsonString(), Encoding.UTF8, "application/json");
        else if (textBody is not null) request.Content = new StringContent(textBody, Encoding.UTF8);
        foreach (var (key, value) in headers)
            if (!request.Headers.TryAddWithoutValidation(key, value)) request.Content?.Headers.TryAddWithoutValidation(key, value);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await Client.SendAsync(request, cts.Token);
            return new FlowHttpResponse((int)response.StatusCode, response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FlowHttpException("The request timed out.");
        }
        catch (HttpRequestException e)
        {
            throw new FlowHttpException(e.Message);
        }
    }
}
