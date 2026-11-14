namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>HDFC Securities' InvestRight stream. The scrip prefix is the exchange, except currency, which is NCD.</summary>
public sealed class HdfcSecuritiesFeedCodec() : IFeedCodec
{
    private readonly GenericDtoFeedCodec inner = new("hdfcsecurities", Prefix);

    internal static string Prefix(string exchange) => exchange == "CDS" ? "NCD" : exchange;

    public string Broker => inner.Broker;

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs) => inner.Subscribe(requests, nowMs);

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs) => inner.Unsubscribe(target, mode, nowMs);

    public IReadOnlyList<string> Resubscribe(long nowMs) => inner.Resubscribe(nowMs);

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs) => inner.DecodeBinary(frame, nowMs);

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => inner.DecodeText(frame, nowMs);

    public void Reset() => inner.Reset();
}
