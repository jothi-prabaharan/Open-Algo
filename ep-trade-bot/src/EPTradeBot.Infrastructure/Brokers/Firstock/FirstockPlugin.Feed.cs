using System.Text;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Firstock;

/// <summary>
/// Firstock's stream (broker/firstock/streaming): one address, with the user id (the API key without its _API ending), the session token (jKey) and the source in its query. The connection is kept alive with a ping every thirty
/// seconds, which the feed host does for every WebSocket. The server accepts or refuses the sign-in with the first frame it sends.
/// </summary>
public sealed partial class FirstockPlugin : IBrokerFeed
{
    private const string FeedUrl = "wss://socket.firstock.in/V2/ws";

    public IFeedCodec CreateFeedCodec() => new FirstockFeedCodec();

    /// <summary>Python's urlencode: letters, digits and _ . - ~ stay, a space is a plus, every other byte is its percent form.</summary>
    private static string QueryValue(string value)
    {
        var text = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~')) text.Append(c);
            else if (c == ' ') text.Append('+');
            else text.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        var user = (session.Key("ApiKey") ?? "").Replace("_API", "", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(session.AuthToken) || string.IsNullOrWhiteSpace(user)) return null;
        return new FeedEndpoint(new Uri($"{overrideUrl ?? FeedUrl}?userId={QueryValue(user)}&jKey={QueryValue(session.AuthToken)}&source=developer-api"), new Dictionary<string, string>());
    }

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(30);
}
