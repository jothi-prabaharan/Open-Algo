using System.Buffers.Binary;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Paytm Money's broadcast stream (broker/paytm/streaming/paytm_websocket.py and paytm_adapter.py). One JSON array of preferences subscribes any
/// mix of modes; frames are little-endian binary led by a packet code, with float32 prices.
/// </summary>
public sealed class PaytmFeedCodec : IFeedCodec
{
    private static readonly Dictionary<string, string> ExchangeTypes = new(StringComparer.Ordinal)
    {
        ["NSE"] = "NSE", ["NFO"] = "NFO", ["BSE"] = "BSE", ["BFO"] = "BFO", ["MCX"] = "MCX", ["CDS"] = "CDS", ["NSE_INDEX"] = "NSE", ["BSE_INDEX"] = "BSE",
    };

    private sealed record Entry(string Key, string Symbol, string Exchange, string Token, int Mode, JsonObject Preference);

    private readonly Lock gate = new();
    private readonly List<Entry> subscriptions = [];
    private readonly Dictionary<string, (string Symbol, string Exchange, int Mode)> tokenMap = [];

    public string Broker => "paytm";

    public static string ModeType(StreamMode mode) => mode switch { StreamMode.Ltp => "LTP", StreamMode.Quote => "QUOTE", _ => "FULL" };

    /// <summary>The scrip type Paytm wants, read from the symbol and exchange.</summary>
    public static string ScripType(string symbol, string exchange)
    {
        if (exchange is "NSE_INDEX" or "BSE_INDEX") return "INDEX";
        if (exchange is "NSE" or "BSE" && (symbol.StartsWith("NIFTY", StringComparison.Ordinal) || symbol.StartsWith("SENSEX", StringComparison.Ordinal)
            || symbol.StartsWith("BANKNIFTY", StringComparison.Ordinal) || symbol.StartsWith("FINNIFTY", StringComparison.Ordinal))) return "INDEX";
        if (exchange is "NFO" or "BFO") return symbol.Contains("CE", StringComparison.Ordinal) || symbol.Contains("PE", StringComparison.Ordinal) ? "OPTION" : "FUTURE";
        return symbol.Contains("ETF", StringComparison.OrdinalIgnoreCase) ? "ETF" : "EQUITY";
    }

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        var preferences = new JsonArray();
        lock (gate)
        {
            foreach (var r in requests)
            {
                var preference = new JsonObject
                {
                    ["actionType"] = "ADD", ["modeType"] = ModeType(r.Mode), ["scripType"] = ScripType(r.Symbol, r.Exchange),
                    ["exchangeType"] = ExchangeTypes.GetValueOrDefault(r.BrokerExchange, "NSE"), ["scripId"] = r.Token,
                };
                var key = $"{r.Symbol}_{r.Exchange}_{(int)r.Mode}";
                var entry = new Entry(key, r.Symbol, r.Exchange, r.Token, (int)r.Mode, preference);
                var at = subscriptions.FindIndex(s => s.Key == key);
                if (at >= 0) subscriptions[at] = entry;
                else subscriptions.Add(entry);
                tokenMap[r.Token] = (r.Symbol, r.Exchange, (int)r.Mode);
                preferences.Add(preference.DeepClone());
            }
        }

        return preferences.Count == 0 ? [] : [preferences.ToJsonString()];
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        var messages = new List<string>();
        lock (gate)
        {
            var found = subscriptions.Where(s => s.Exchange == target.Exchange && s.Symbol == target.Symbol && (mode is null || s.Mode == (int)mode)).ToList();
            foreach (var entry in found)
            {
                subscriptions.Remove(entry);
                var removal = (JsonObject)entry.Preference.DeepClone();
                removal["actionType"] = "REMOVE";
                messages.Add(new JsonArray(removal).ToJsonString());
            }

            // The token stays known while another mode of it is subscribed.
            if (found.Count > 0 && !subscriptions.Any(s => s.Token == target.Token)) tokenMap.Remove(target.Token);
            else if (found.Count > 0 && subscriptions.LastOrDefault(s => s.Token == target.Token) is { } last) tokenMap[target.Token] = (last.Symbol, last.Exchange, last.Mode);
        }

        return messages;
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        lock (gate) return subscriptions.Count == 0 ? [] : [new JsonArray(subscriptions.Select(s => (JsonNode?)s.Preference.DeepClone()).ToArray()).ToJsonString()];
    }

    public void Reset()
    {
        lock (gate)
        {
            subscriptions.Clear();
            tokenMap.Clear();
        }
    }

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- binary frames ----------------------------------------------------------------------------------------

    private const int Ltp = 61, Quote = 62, Full = 63, IndexLtp = 64, IndexQuote = 65, IndexFull = 66;

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        if (frame.Length < 1) return [];
        var d = frame.ToArray();
        JsonObject message;
        try
        {
            if (Parse(d) is not { } parsed) return [];
            message = parsed;
        }
        catch (ArgumentOutOfRangeException)
        {
            return [];
        }

        var token = message["security_id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        (string Symbol, string Exchange, int Mode) known;
        lock (gate)
        {
            if (!tokenMap.TryGetValue(token, out known)) return [];
        }

        var subscriptionMode = message["subscription_mode"]!.GetValue<int>();
        var data = Normalize(message, subscriptionMode);
        data["symbol"] = known.Symbol;
        data["exchange"] = known.Exchange;
        data["mode"] = subscriptionMode;
        data["timestamp"] = nowMs;
        return [new FeedPublish(MarketTopics.Format(known.Exchange, known.Symbol, (StreamMode)subscriptionMode), data)];
    }

    private static uint U(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at, 4));

    private static int I(byte[] d, int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at, 4));

    private static short S(byte[] d, int at) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(at, 2));

    private static double F(byte[] d, int at) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at, 4));

    private static JsonObject? Parse(byte[] d)
    {
        switch (d[0])
        {
            case Ltp or IndexLtp:
                return new JsonObject
                {
                    ["last_price"] = F(d, 1), ["last_traded_time"] = (long)U(d, 5), ["security_id"] = (long)U(d, 9), ["change_absolute"] = F(d, 15), ["change_percent"] = F(d, 19),
                    ["subscription_mode"] = 1,
                };
            case Quote:
                return new JsonObject
                {
                    ["last_price"] = F(d, 1), ["last_traded_time"] = (long)U(d, 5), ["security_id"] = (long)U(d, 9), ["last_traded_quantity"] = (long)U(d, 15),
                    ["average_traded_price"] = F(d, 19), ["volume_traded"] = (long)U(d, 23), ["total_buy_quantity"] = (long)U(d, 27), ["total_sell_quantity"] = (long)U(d, 31),
                    ["open"] = F(d, 35), ["close"] = F(d, 39), ["high"] = F(d, 43), ["low"] = F(d, 47), ["change_percent"] = F(d, 51), ["change_absolute"] = F(d, 55),
                    ["52_week_high"] = F(d, 59), ["52_week_low"] = F(d, 63), ["subscription_mode"] = 2,
                };
            case IndexQuote:
                return new JsonObject
                {
                    ["last_price"] = F(d, 1), ["security_id"] = (long)U(d, 5), ["open"] = F(d, 11), ["close"] = F(d, 15), ["high"] = F(d, 19), ["low"] = F(d, 23),
                    ["change_absolute"] = F(d, 27), ["change_percent"] = F(d, 31), ["52_week_high"] = F(d, 35), ["52_week_low"] = F(d, 39), ["subscription_mode"] = 2,
                };
            case Full:
                var buy = new JsonArray();
                var sell = new JsonArray();
                for (var i = 0; i < 5; i++)
                {
                    var o = 1 + (i * 20);
                    buy.Add(new JsonObject { ["quantity"] = I(d, o), ["price"] = F(d, o + 12), ["orders"] = (int)S(d, o + 8) });
                    sell.Add(new JsonObject { ["quantity"] = I(d, o + 4), ["price"] = F(d, o + 16), ["orders"] = (int)S(d, o + 10) });
                }

                var full = new JsonObject
                {
                    ["last_price"] = F(d, 101), ["last_traded_time"] = (long)U(d, 105), ["security_id"] = (long)U(d, 109), ["last_traded_quantity"] = (long)U(d, 115),
                    ["average_traded_price"] = F(d, 119), ["volume_traded"] = (long)U(d, 123), ["total_buy_quantity"] = (long)U(d, 127), ["total_sell_quantity"] = (long)U(d, 131),
                    ["open"] = F(d, 135), ["close"] = F(d, 139), ["high"] = F(d, 143), ["low"] = F(d, 147), ["change_percent"] = F(d, 151), ["change_absolute"] = F(d, 155),
                    ["52_week_high"] = F(d, 159), ["52_week_low"] = F(d, 163), ["subscription_mode"] = 3,
                    ["depth"] = new JsonObject { ["buy"] = buy, ["sell"] = sell },
                };
                if (d.Length >= 175)
                {
                    full["oi"] = (long)U(d, 167);
                    full["oi_change"] = (long)U(d, 171);
                }

                return full;
            case IndexFull:
                return new JsonObject
                {
                    ["last_price"] = F(d, 1), ["security_id"] = (long)U(d, 5), ["open"] = F(d, 11), ["close"] = F(d, 15), ["high"] = F(d, 19), ["low"] = F(d, 23),
                    ["change_percent"] = F(d, 27), ["change_absolute"] = F(d, 31), ["subscription_mode"] = 3,
                };
            default:
                return null;
        }
    }

    private static double R(JsonObject m, string key) => BookFormatting.RoundPy(m[key]?.GetValue<double>() ?? 0, 2);

    private static JsonNode N(JsonObject m, string key) => m[key]?.DeepClone() ?? 0;

    private static JsonObject Normalize(JsonObject m, int mode)
    {
        if (mode == 1)
        {
            var time = m["last_traded_time"]?.GetValue<long>() ?? 0;
            return new JsonObject { ["ltp"] = R(m, "last_price"), ["ltt"] = time != 0 ? time : 0, ["change_absolute"] = R(m, "change_absolute"), ["change_percent"] = R(m, "change_percent") };
        }

        var result = new JsonObject
        {
            ["ltp"] = R(m, "last_price"), ["ltt"] = N(m, "last_traded_time"), ["volume"] = N(m, "volume_traded"),
            ["open"] = R(m, "open"), ["high"] = R(m, "high"), ["low"] = R(m, "low"), ["close"] = R(m, "close"),
            [mode == 2 ? "last_trade_quantity" : "last_quantity"] = N(m, "last_traded_quantity"),
            ["average_price"] = R(m, "average_traded_price"), ["total_buy_quantity"] = N(m, "total_buy_quantity"), ["total_sell_quantity"] = N(m, "total_sell_quantity"),
            ["change_absolute"] = R(m, "change_absolute"), ["change_percent"] = R(m, "change_percent"),
            ["52_week_high"] = R(m, "52_week_high"), ["52_week_low"] = R(m, "52_week_low"),
        };
        if (mode != 3) return result;
        if (m.ContainsKey("oi"))
        {
            result["oi"] = N(m, "oi");
            result["oi_change"] = N(m, "oi_change");
        }

        if (m["depth"] is JsonObject depth)
        {
            JsonArray Levels(JsonArray side) => new(side.OfType<JsonObject>().Select(l => (JsonNode?)new JsonObject
            {
                ["price"] = BookFormatting.RoundPy(l["price"]!.GetValue<double>(), 2), ["quantity"] = l["quantity"]!.DeepClone(), ["orders"] = l["orders"]!.DeepClone(),
            }).ToArray());
            result["depth"] = new JsonObject { ["buy"] = Levels((JsonArray)depth["buy"]!), ["sell"] = Levels((JsonArray)depth["sell"]!) };
        }

        return result;
    }
}
