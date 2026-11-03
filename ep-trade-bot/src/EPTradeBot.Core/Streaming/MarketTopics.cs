namespace EPTradeBot.Core.Streaming;

/// <summary>Subscription depth: 1 LTP, 2 Quote, 3 Depth. A higher mode carries everything a lower one does.</summary>
public enum StreamMode
{
    Ltp = 1,
    Quote = 2,
    Depth = 3,
}

/// <summary>One parsed bus topic.</summary>
public sealed record MarketTopic(string Exchange, string Symbol, StreamMode Mode);

/// <summary>
/// Port of OpenAlgo's websocket_proxy/mode_utils.py and the topic parser in
/// server.py. Topics are EXCHANGE_SYMBOL_MODE; the mode is always the last
/// segment, two-segment exchanges (NSE_INDEX and the like) are recognised, and a
/// symbol may itself contain underscores (crypto pairs such as SOL_INR).
/// </summary>
public static class MarketTopics
{
    private static readonly HashSet<(string, string)> MultiSegmentExchanges =
        [("NSE", "INDEX"), ("BSE", "INDEX"), ("MCX", "INDEX"), ("GLOBAL", "INDEX")];

    /// <summary>The canonical label echoed to clients: LTP, Quote, Depth.</summary>
    public static string Label(StreamMode mode) => mode switch
    {
        StreamMode.Ltp => "LTP",
        StreamMode.Quote => "Quote",
        _ => "Depth",
    };

    /// <summary>normalize_mode: 1/2/3 or LTP/Quote/Depth in any case. Null for anything else.</summary>
    public static StreamMode? ParseMode(object? value) => value switch
    {
        int i and >= 1 and <= 3 => (StreamMode)i,
        long l and >= 1 and <= 3 => (StreamMode)(int)l,
        string s => s.Trim().ToUpperInvariant() switch
        {
            "LTP" => StreamMode.Ltp,
            "QUOTE" => StreamMode.Quote,
            "DEPTH" => StreamMode.Depth,
            _ => null,
        },
        _ => null,
    };

    public static string Format(string exchange, string symbol, StreamMode mode) =>
        $"{exchange}_{symbol}_{(mode == StreamMode.Ltp ? "LTP" : mode == StreamMode.Quote ? "QUOTE" : "DEPTH")}";

    /// <summary>Parses a market-data topic; null for a malformed topic or one that is not market data.</summary>
    public static MarketTopic? Parse(string topic)
    {
        var parts = topic.Split('_');
        if (parts.Length < 3) return null;
        if (ParseMode(parts[^1]) is not { } mode) return null;
        var rest = parts[..^1];
        string exchange, symbol;
        if (rest.Length >= 2 && MultiSegmentExchanges.Contains((rest[0], rest[1])))
        {
            exchange = $"{rest[0]}_{rest[1]}";
            symbol = string.Join('_', rest[2..]);
        }
        else
        {
            exchange = rest[0];
            symbol = string.Join('_', rest[1..]);
        }

        return symbol.Length == 0 ? null : new MarketTopic(exchange, symbol, mode);
    }
}
