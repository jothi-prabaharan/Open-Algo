using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// Zerodha's Kite ticker (broker/zerodha/streaming/zerodha_websocket.py and zerodha_adapter.py). Kite carries ONE mode per token, so the highest mode
/// asked for is requested and its tick, a superset of the lower modes, is fanned out to every mode subscribed.
/// </summary>
public sealed class KiteFeedCodec : IFeedCodec
{
    private const int MaxTokensPerMessage = 200;
    private static readonly string[] IndexExchanges = ["NSE_INDEX", "BSE_INDEX", "MCX_INDEX", "GLOBAL_INDEX"];

    private readonly Lock gate = new();
    private readonly Dictionary<(string Exchange, string Symbol, StreamMode Mode), long> subscribed = [];
    private readonly Dictionary<long, (string Symbol, string Exchange)> tokenToSymbol = [];
    private readonly Dictionary<long, string> kiteMode = [];

    public string Broker => "zerodha";

    /// <summary>The token Kite means: the master contract holds "instrument_token::::exchange_token".</summary>
    public static long? InstrumentToken(string token) =>
        long.TryParse(token.Contains("::::", StringComparison.Ordinal) ? token.Split("::::")[0] : token.Contains(':') ? token.Split(':')[0] : token, out var t) ? t : null;

    private static string ModeName(StreamMode mode) => mode switch { StreamMode.Ltp => "ltp", StreamMode.Quote => "quote", _ => "full" };

    public IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs)
    {
        List<(long Token, string Mode)> queue = [];
        lock (gate)
        {
            foreach (var r in requests)
            {
                if (InstrumentToken(r.Token) is not { } token) continue;
                subscribed[(r.Exchange, r.Symbol, r.Mode)] = token;
                tokenToSymbol[token] = (r.Symbol, r.Exchange);
                var highest = subscribed.Where(s => s.Value == token).Max(s => (int)s.Key.Mode);
                queue.Add((token, ModeName((StreamMode)highest)));
            }
        }

        // Grouped by the mode asked of Kite, then sent in batches. Each batch is a subscribe followed by the mode for those tokens.
        var messages = new List<string>();
        foreach (var group in queue.GroupBy(q => q.Mode))
        {
            foreach (var batch in group.Select(g => g.Token).Chunk(MaxTokensPerMessage))
            {
                messages.Add(new JsonObject { ["a"] = "subscribe", ["v"] = Tokens(batch) }.ToJsonString());
                messages.Add(new JsonObject { ["a"] = "mode", ["v"] = new JsonArray(group.Key, Tokens(batch)) }.ToJsonString());
                lock (gate) foreach (var t in batch) kiteMode[t] = group.Key;
            }
        }

        return messages;
    }

    public IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs)
    {
        lock (gate)
        {
            var keys = subscribed.Keys.Where(k => k.Exchange == target.Exchange && k.Symbol == target.Symbol && (mode is null || k.Mode == mode)).ToList();
            if (keys.Count == 0) return [];
            var token = subscribed[keys[0]];
            foreach (var k in keys) subscribed.Remove(k);
            // The Kite stream is dropped only when no mode still needs the token; a lower mode left behind keeps the existing, richer stream.
            if (subscribed.Values.Contains(token)) return [];
            tokenToSymbol.Remove(token);
            kiteMode.Remove(token);
            return [new JsonObject { ["a"] = "unsubscribe", ["v"] = Tokens([token]) }.ToJsonString()];
        }
    }

    public IReadOnlyList<string> Resubscribe(long nowMs)
    {
        List<(long Token, string Mode)> all;
        lock (gate) all = kiteMode.Select(kv => (kv.Key, kv.Value)).ToList();
        var messages = new List<string>();
        foreach (var group in all.GroupBy(a => a.Mode))
        {
            foreach (var batch in group.Select(g => g.Token).Chunk(MaxTokensPerMessage))
            {
                messages.Add(new JsonObject { ["a"] = "subscribe", ["v"] = Tokens(batch) }.ToJsonString());
                messages.Add(new JsonObject { ["a"] = "mode", ["v"] = new JsonArray(group.Key, Tokens(batch)) }.ToJsonString());
            }
        }

        return messages;
    }

    public void Reset()
    {
        lock (gate)
        {
            subscribed.Clear();
            tokenToSymbol.Clear();
            kiteMode.Clear();
        }
    }

    private static JsonArray Tokens(IEnumerable<long> tokens) => new(tokens.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());

    public IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs) => [];

    // ---- binary frames ----------------------------------------------------------------------------------------

    public IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs)
    {
        var published = new List<FeedPublish>();
        // A single byte is Kite's heartbeat.
        if (frame.Length < 4) return published;
        var count = BinaryPrimitives.ReadUInt16BigEndian(frame);
        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            if (offset + 2 > frame.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(frame[offset..]);
            offset += 2;
            if (offset + length > frame.Length) break;
            if (ParsePacket(frame.Slice(offset, length), nowMs) is { } tick) Publish(tick, published, nowMs);
            offset += length;
        }

        return published;
    }

    private JsonObject? ParsePacket(ReadOnlySpan<byte> span, long nowMs)
    {
        var packet = span.ToArray();
        if (packet.Length < 8) return null;
        var token = (long)BinaryPrimitives.ReadUInt32BigEndian(packet);
        var lastPrice = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(4)) / 100.0;
        string mode;
        if (packet.Length == 8) mode = "ltp";
        else if (packet.Length == 44) mode = "quote";
        else if (packet.Length >= 184) mode = "full";
        else
        {
            lock (gate) mode = kiteMode.GetValueOrDefault(token, "quote");
        }

        var tick = new JsonObject
        {
            ["instrument_token"] = token, ["last_traded_price"] = lastPrice, ["last_price"] = lastPrice, ["mode"] = mode, ["timestamp"] = nowMs,
        };

        if (packet.Length >= 44)
        {
            int Field(int i) => BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(i * 4));
            tick["instrument_token"] = (long)(uint)Field(0);
            tick["last_traded_price"] = Field(1) / 100.0;
            tick["last_price"] = Field(1) / 100.0;
            tick["last_traded_quantity"] = Field(2);
            tick["average_traded_price"] = Field(3) / 100.0;
            tick["average_price"] = Field(3) / 100.0;
            tick["volume_traded"] = Field(4);
            tick["volume"] = Field(4);
            tick["total_buy_quantity"] = Field(5);
            tick["total_sell_quantity"] = Field(6);
            tick["open_price"] = Field(7) / 100.0;
            tick["high_price"] = Field(8) / 100.0;
            tick["low_price"] = Field(9) / 100.0;
            tick["close_price"] = Field(10) / 100.0;
            tick["ohlc"] = new JsonObject { ["open"] = Field(7) / 100.0, ["high"] = Field(8) / 100.0, ["low"] = Field(9) / 100.0, ["close"] = Field(10) / 100.0 };
        }

        if (packet.Length >= 184)
        {
            tick["price_change"] = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(44)) / 100.0;
            JsonArray Side(int start)
            {
                var levels = new JsonArray();
                for (var i = 0; i < 5; i++)
                {
                    var b = start + (i * 12);
                    if (b + 12 > packet.Length) continue;
                    levels.Add(new JsonObject
                    {
                        ["quantity"] = (long)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(b)),
                        ["price"] = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(b + 4)) / 100.0,
                        ["orders"] = (int)BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(b + 8)),
                    });
                }

                return levels;
            }

            tick["depth"] = new JsonObject { ["buy"] = Side(64), ["sell"] = Side(124) };
            tick["exchange_timestamp"] = (long)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(60));
            tick["open_interest"] = (long)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(180));
        }

        return tick;
    }

    // ---- from a Kite tick to what clients receive ------------------------------------------------------------------

    private void Publish(JsonObject tick, List<FeedPublish> published, long nowMs)
    {
        var token = tick["instrument_token"]!.GetValue<long>();
        string symbol, exchange;
        HashSet<int> modes = [];
        lock (gate)
        {
            if (!tokenToSymbol.TryGetValue(token, out var known)) return;
            (symbol, exchange) = known;
            foreach (var (key, value) in subscribed) if (value == token) modes.Add((int)key.Mode);
        }

        // No subscription for the token: nothing to publish.
        if (modes.Count == 0) return;
        var mode = tick["mode"]!.GetValue<string>();
        var transformed = IndexExchanges.Contains(exchange) ? Transform(tick, symbol, exchange, mode, nowMs, index: true) : Transform(tick, symbol, exchange, mode, nowMs, index: false);
        transformed["exchange"] = exchange;

        string Topic(string m) => MarketTopics.Format(exchange, symbol, m switch { "LTP" => StreamMode.Ltp, "QUOTE" => StreamMode.Quote, _ => StreamMode.Depth });
        JsonObject LtpTick() => new()
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = "ltp",
            ["ltp"] = transformed["ltp"]?.DeepClone() ?? 0, ["timestamp"] = transformed["timestamp"]?.DeepClone() ?? nowMs,
        };

        if (mode == "full")
        {
            // The depth tick first, then the lower modes subscribed.
            var depth = (JsonObject)transformed.DeepClone();
            depth["mode"] = "full";
            published.Add(new FeedPublish(Topic("DEPTH"), depth));
            if (modes.Contains(2))
            {
                var quote = (JsonObject)transformed.DeepClone();
                quote.Remove("depth");
                quote["mode"] = "quote";
                published.Add(new FeedPublish(Topic("QUOTE"), quote));
            }

            if (modes.Contains(1)) published.Add(new FeedPublish(Topic("LTP"), LtpTick()));
            return;
        }

        published.Add(new FeedPublish(Topic(mode switch { "ltp" => "LTP", "quote" => "QUOTE", _ => "LTP" }), transformed));
        // A quote tick is a superset of LTP, so the LTP topic is fed too when both are subscribed.
        if (mode == "quote" && modes.Contains(1)) published.Add(new FeedPublish(Topic("LTP"), LtpTick()));
    }

    private static JsonNode Get(JsonObject tick, string key, JsonNode? fallback) => tick[key]?.DeepClone() ?? fallback?.DeepClone() ?? 0;

    private static JsonObject Transform(JsonObject tick, string symbol, string exchange, string mode, long nowMs, bool index)
    {
        var ltp = tick["last_traded_price"]?.DeepClone() ?? tick["last_price"]?.DeepClone() ?? 0;
        var ltt = tick["exchange_timestamp"]?.DeepClone() ?? tick["timestamp"]?.DeepClone() ?? nowMs;
        var timestamp = tick["timestamp"]?.DeepClone() ?? nowMs;
        if (mode == "ltp")
            return new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = mode, ["ltp"] = ltp, ["ltt"] = ltt, ["timestamp"] = timestamp };

        if (mode is not ("quote" or "full"))
            return new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = mode, ["ltp"] = ltp, ["ltt"] = tick["timestamp"]?.DeepClone() ?? nowMs };

        var volume = tick["volume_traded"]?.DeepClone() ?? tick["volume"]?.DeepClone() ?? 0;
        var result = new JsonObject
        {
            ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = mode, ["ltp"] = ltp, ["ltt"] = ltt, ["timestamp"] = timestamp, ["volume"] = volume,
        };
        if (index)
        {
            result["price_change"] = Get(tick, "price_change", 0);
            result["price_change_percent"] = Get(tick, "price_change_percent", 0);
        }
        else
        {
            result["last_quantity"] = Get(tick, "last_traded_quantity", 0);
            result["average_price"] = tick["average_traded_price"]?.DeepClone() ?? tick["average_price"]?.DeepClone() ?? 0;
            result["total_buy_quantity"] = Get(tick, "total_buy_quantity", 0);
            result["total_sell_quantity"] = Get(tick, "total_sell_quantity", 0);
        }

        if (tick["ohlc"] is JsonObject ohlc)
        {
            result["open"] = Get(ohlc, "open", 0);
            result["high"] = Get(ohlc, "high", 0);
            result["low"] = Get(ohlc, "low", 0);
            result["close"] = Get(ohlc, "close", 0);
        }
        else
        {
            if (tick.ContainsKey("open_price")) result["open"] = tick["open_price"]!.DeepClone();
            if (tick.ContainsKey("high_price")) result["high"] = tick["high_price"]!.DeepClone();
            if (tick.ContainsKey("low_price")) result["low"] = tick["low_price"]!.DeepClone();
            if (tick.ContainsKey("close_price")) result["close"] = tick["close_price"]!.DeepClone();
        }

        if (index)
        {
            if (tick.ContainsKey("exchange_timestamp")) result["exchange_timestamp"] = tick["exchange_timestamp"]!.DeepClone();
            return result;
        }

        if (tick.ContainsKey("open_interest"))
        {
            result["oi"] = tick["open_interest"]!.DeepClone();
            result["open_interest"] = tick["open_interest"]!.DeepClone();
        }

        if (mode == "full" && tick["depth"] is JsonObject depth && depth["buy"] is JsonArray buy && depth["sell"] is JsonArray sell)
        {
            JsonArray Levels(JsonArray side) => new(side.OfType<JsonObject>().Take(5).Select(l => (JsonNode?)new JsonObject
            {
                ["price"] = Get(l, "price", 0), ["quantity"] = Get(l, "quantity", 0), ["orders"] = Get(l, "orders", 0),
            }).ToArray());
            result["depth"] = new JsonObject { ["buy"] = Levels(buy), ["sell"] = Levels(sell) };
        }

        return result;
    }
}
