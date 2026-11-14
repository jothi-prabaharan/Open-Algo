using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Samco;

/// <summary>
/// Samco's stream (broker/samco/streaming): wss://stream.samco.in, the session token (URL-decoded) in the x-session-token header. An index is streamed under a listing id that only /quote/indexQuote gives, so the
/// codec asks for it through <see cref="SendFeedCallAsync"/> and reads the answer as a text frame of its own.
/// </summary>
public sealed partial class SamcoPlugin : IBrokerFeedHttp
{
    public IFeedCodec CreateFeedCodec() => new SamcoFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken)
            ? null
            : new FeedEndpoint(new Uri(overrideUrl ?? "wss://stream.samco.in"), new Dictionary<string, string> { ["x-session-token"] = Uri.UnescapeDataString(session.AuthToken) });

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(30);

    /// <summary>Asks for an index's listing id. The answer is one text frame: the symbol, the exchange and the listing id (null when Samco gave none).</summary>
    public async Task<IReadOnlyList<string>> SendFeedCallAsync(BrokerSession session, FeedHttpCall call, CancellationToken cancellationToken)
    {
        string? listing = null;
        try
        {
            var (_, answer) = await SendAsync(session, HttpMethod.Get, call.Path, null, cancellationToken);
            if (Succeeded(answer) && (answer["indexDetails"] as JsonArray)?.FirstOrDefault() is JsonObject first && Truthy(first["listingId"])) listing = Str(first["listingId"]);
        }
        catch (BrokerException)
        {
        }

        return [new JsonObject { ["samco_index"] = true, ["symbol"] = call.Body["symbol"]?.DeepClone(), ["exchange"] = call.Body["exchange"]?.DeepClone(), ["listing_id"] = listing }.ToJsonString()];
    }
}
