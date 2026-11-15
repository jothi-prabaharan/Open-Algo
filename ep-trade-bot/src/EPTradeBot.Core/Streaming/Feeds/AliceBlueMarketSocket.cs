using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>An instrument as the market-data socket names it: the exchange (an index on its cash exchange) and the token as a whole number.</summary>
public readonly record struct MarketInstrument(string Exchange, string Token)
{
    public string Key => $"{Exchange}|{Token}";
}

/// <summary>
/// The market-data socket AliceBlue's quotes and depth are read from (broker/aliceblue api/alicebluewebsocket.py), without the network: what it sends goes through <c>send</c> and what arrives is given to <see cref="OnMessage"/>. It subscribes
/// instruments and counts who wants each, so one request giving an instrument back does not end the subscription another still waits on; keeps the last quote and the last depth of each instrument, merged from the acknowledgement and the feed
/// ticks; and when the last subscriber of an instrument goes, unsubscribes it and drops what was kept for it. A message it cannot read is dropped whole.
/// </summary>
public sealed class AliceBlueMarketSocket(Action<string> send)
{
    private sealed class Level
    {
        public double Price { get; set; }

        public long Quantity { get; set; }

        public long Orders { get; set; }
    }

    private sealed class DepthState
    {
        public List<Level> Bids { get; set; } = [];

        public List<Level> Asks { get; set; } = [];

        public Dictionary<string, double> Numbers { get; set; } = [];
    }

    private readonly Lock gate = new();
    private readonly HashSet<string> subscribed = [];
    private readonly Dictionary<string, int> references = [];
    private readonly Dictionary<string, JsonObject> quotes = [];
    private readonly Dictionary<string, DepthState> depths = [];

    public bool Connected { get; set; }

    private static string Cache(MarketInstrument i) => $"{i.Exchange}:{i.Token}";

    // ---- subscribing -------------------------------------------------------------------------------------------

    public bool Subscribe(IReadOnlyList<MarketInstrument> instruments, bool depth)
    {
        lock (gate)
        {
            if (!Connected || instruments.Count == 0) return false;
            foreach (var i in instruments)
            {
                subscribed.Add(i.Key);
                references[i.Key] = references.GetValueOrDefault(i.Key) + 1;
            }

            try
            {
                send(new JsonObject { ["t"] = depth ? "d" : "t", ["k"] = string.Join('#', instruments.Select(i => i.Key)) }.ToJsonString());
            }
            catch (Exception)
            {
                Release(instruments);
                return false;
            }

            return true;
        }
    }

    /// <summary>Takes one subscriber off each instrument and gives back the keys nobody wants any more, with what was kept for them.</summary>
    private List<string> Release(IReadOnlyList<MarketInstrument> instruments)
    {
        var released = new List<string>();
        foreach (var i in instruments)
        {
            var remaining = references.GetValueOrDefault(i.Key) - 1;
            if (remaining > 0)
            {
                references[i.Key] = remaining;
                continue;
            }

            references.Remove(i.Key);
            subscribed.Remove(i.Key);
            quotes.Remove(Cache(i));
            depths.Remove(Cache(i));
            released.Add(i.Key);
        }

        return released;
    }

    public bool Unsubscribe(IReadOnlyList<MarketInstrument> instruments)
    {
        lock (gate)
        {
            if (instruments.Count == 0) return false;
            var released = Release(instruments);
            if (!Connected) return false;
            if (released.Count == 0) return true;
            try
            {
                send(new JsonObject { ["t"] = "u", ["k"] = string.Join('#', released) }.ToJsonString());
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }
    }

    public JsonObject? GetQuote(string exchange, string token)
    {
        lock (gate) return quotes.TryGetValue($"{exchange}:{token}", out var quote) ? (JsonObject)quote.DeepClone() : null;
    }

    /// <summary>The depth kept for an instrument as <c>{bids, asks, numbers}</c>, or null when none has arrived.</summary>
    public JsonObject? GetDepth(string exchange, string token)
    {
        lock (gate)
        {
            if (!depths.TryGetValue($"{exchange}:{token}", out var state)) return null;
            JsonArray Side(List<Level> side) => new(side.Select(l => (JsonNode)new JsonObject { ["price"] = l.Price, ["quantity"] = l.Quantity, ["orders"] = l.Orders }).ToArray());
            var result = new JsonObject { ["bids"] = Side(state.Bids), ["asks"] = Side(state.Asks) };
            foreach (var (name, value) in state.Numbers) result[name] = name is "volume" or "last_trade_quantity" or "total_buy_quantity" or "total_sell_quantity" or "open_interest" or "prev_open_interest" ? (long)value : value;
            return result;
        }
    }

    // ---- reading -----------------------------------------------------------------------------------------------

    private static string? Text(JsonObject message, string name) => message.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public void OnMessage(string text)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (parsed is not JsonObject data) return;
        lock (gate)
        {
            try
            {
                if (Text(data, "s") == "OK")
                {
                    Connected = true;
                    Resubscribe();
                }
                else if (Text(data, "t") == "cf")
                {
                    if (Text(data, "k") == "OK") Connected = true;
                }
                else if (Text(data, "t") is "tk" or "tf")
                {
                    OnTick(data);
                }
                else if (Text(data, "t") is "dk" or "df")
                {
                    OnDepth(data);
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                // A number the feed cannot read stops the message where it is.
            }
        }
    }

    private void Resubscribe()
    {
        if (subscribed.Count == 0) return;
        var keys = string.Join('#', subscribed);
        send(new JsonObject { ["k"] = keys, ["t"] = "t" }.ToJsonString());
        send(new JsonObject { ["k"] = keys, ["t"] = "d" }.ToJsonString());
    }

    private static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is JsonArray a) return a.Count > 0;
        if (node is JsonObject o) return o.Count > 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var text)) return text.Length > 0;
            if (v.TryGetValue<bool>(out var flag)) return flag;
            if (v.TryGetValue<double>(out var d)) return d != 0;
        }

        return true;
    }

    private static double PyFloat(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
            if (v.TryGetValue<string>(out var text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }

        throw new FormatException("Not a number.");
    }

    private static long PyInt(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d))
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("Not an integer.");
                return (long)Math.Truncate(d);
            }

            if (v.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
            if (v.TryGetValue<string>(out var text) && long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }

        throw new FormatException("Not an integer.");
    }

    private static JsonNode? Field(JsonObject data, string name) => data.TryGetPropertyValue(name, out var node) ? node : null;

    /// <summary>float(data.get(name, 0)): absent is zero, anything present has to read as a number.</summary>
    private static double F(JsonObject data, string name) => data.ContainsKey(name) ? PyFloat(data[name]) : 0;

    private static long I(JsonObject data, string name) => data.ContainsKey(name) ? PyInt(data[name]) : 0;

    /// <summary>int(float(x)) if x else 0: an open interest may be text with decimals.</summary>
    private static long Oi(JsonObject data, string name) => Truthy(Field(data, name)) ? (long)Math.Truncate(PyFloat(Field(data, name))) : 0;

    private void OnTick(JsonObject data)
    {
        var exchange = Text(data, "e") ?? "";
        var token = Text(data, "tk") ?? "";
        var key = $"{exchange}:{token}";
        JsonObject quote;
        if (Text(data, "t") == "tk")
        {
            quote = new JsonObject
            {
                ["exchange"] = exchange, ["token"] = token, ["ltp"] = F(data, "lp"), ["open"] = F(data, "o"), ["high"] = F(data, "h"), ["low"] = F(data, "l"), ["close"] = F(data, "c"), ["volume"] = I(data, "v"),
                ["last_trade_time"] = data.ContainsKey("ft") ? Field(data, "ft")?.DeepClone() : "", ["last_trade_quantity"] = I(data, "ltq"), ["average_trade_price"] = F(data, "ap"), ["open_interest"] = Oi(data, "oi"),
                ["prev_open_interest"] = Oi(data, "poi"), ["total_buy_quantity"] = I(data, "tbq"), ["total_sell_quantity"] = I(data, "tsq"), ["bid"] = F(data, "bp1"), ["ask"] = F(data, "sp1"), ["bid_qty"] = I(data, "bq1"),
                ["ask_qty"] = I(data, "sq1"),
            };
        }
        else
        {
            quote = quotes.TryGetValue(key, out var existing) ? (JsonObject)existing.DeepClone() : [];
            quote["exchange"] = exchange;
            quote["token"] = token;
            if (data.ContainsKey("lp")) quote["ltp"] = PyFloat(data["lp"]);
            if (data.ContainsKey("pc")) quote["percent_change"] = PyFloat(data["pc"]);
            if (data.ContainsKey("v")) quote["volume"] = PyInt(data["v"]);
            if (data.ContainsKey("ft")) quote["last_trade_time"] = data["ft"]?.DeepClone();
            if (data.ContainsKey("ltq")) quote["last_trade_quantity"] = PyInt(data["ltq"]);
            if (data.ContainsKey("bp1")) quote["bid"] = PyFloat(data["bp1"]);
            if (data.ContainsKey("sp1")) quote["ask"] = PyFloat(data["sp1"]);
            if (data.ContainsKey("bq1")) quote["bid_qty"] = PyInt(data["bq1"]);
            if (data.ContainsKey("sq1")) quote["ask_qty"] = PyInt(data["sq1"]);
            if (data.ContainsKey("tbq")) quote["total_buy_quantity"] = PyInt(data["tbq"]);
            if (data.ContainsKey("tsq")) quote["total_sell_quantity"] = PyInt(data["tsq"]);
            if (data.ContainsKey("oi")) quote["open_interest"] = Oi(data, "oi");
            if (data.ContainsKey("poi")) quote["prev_open_interest"] = Oi(data, "poi");
        }

        quotes[key] = quote;
    }

    private static readonly (string Source, string Name, bool Whole)[] DepthScalars =
    [
        ("lp", "ltp", false), ("o", "open", false), ("h", "high", false), ("l", "low", false), ("c", "close", false), ("v", "volume", true), ("pc", "percent_change", false), ("ltq", "last_trade_quantity", true),
        ("tbq", "total_buy_quantity", true), ("tsq", "total_sell_quantity", true),
    ];

    private void OnDepth(JsonObject data)
    {
        var exchange = Text(data, "e") ?? "";
        var token = Text(data, "tk") ?? "";
        var key = $"{exchange}:{token}";
        DepthState depth;
        if (Text(data, "t") == "dk")
        {
            var bids = new List<Level>();
            var asks = new List<Level>();
            for (var i = 1; i <= 5; i++)
            {
                var bidPrice = F(data, $"bp{i}");
                var bidQuantity = I(data, $"bq{i}");
                var bidOrders = I(data, $"bo{i}");
                if (bidPrice > 0) bids.Add(new Level { Price = bidPrice, Quantity = bidQuantity, Orders = bidOrders });
                var askPrice = F(data, $"sp{i}");
                var askQuantity = I(data, $"sq{i}");
                var askOrders = I(data, $"so{i}");
                if (askPrice > 0) asks.Add(new Level { Price = askPrice, Quantity = askQuantity, Orders = askOrders });
            }

            depth = new DepthState { Bids = bids, Asks = asks };
            foreach (var (source, name, whole) in DepthScalars)
            {
                // The acknowledgement keeps no percent change.
                if (source != "pc") depth.Numbers[name] = whole ? I(data, source) : F(data, source);
            }

            depth.Numbers["open_interest"] = Oi(data, "oi");
            depth.Numbers["prev_open_interest"] = Oi(data, "poi");
        }
        else
        {
            // The copy shares the held ladders, as the broker's own dictionary copy does: a level changed before a later field fails stays changed.
            var existing = depths.GetValueOrDefault(key);
            depth = new DepthState { Bids = existing?.Bids ?? [], Asks = existing?.Asks ?? [], Numbers = existing is null ? [] : new Dictionary<string, double>(existing.Numbers) };
            foreach (var (source, name, whole) in DepthScalars)
            {
                if (data.ContainsKey(source)) depth.Numbers[name] = whole ? PyInt(data[source]) : PyFloat(data[source]);
            }

            if (data.ContainsKey("oi")) depth.Numbers["open_interest"] = Oi(data, "oi");
            if (data.ContainsKey("poi")) depth.Numbers["prev_open_interest"] = Oi(data, "poi");
            Update(depth.Bids, data, 'b');
            Update(depth.Asks, data, 's');
            depths[key] = depth;
            return;
        }

        depths[key] = depth;
    }

    private static void Update(List<Level> side, JsonObject data, char prefix)
    {
        for (var i = 1; i <= 5; i++)
        {
            if (!(data.ContainsKey($"{prefix}p{i}") || data.ContainsKey($"{prefix}q{i}") || data.ContainsKey($"{prefix}o{i}"))) continue;
            while (side.Count < i) side.Add(new Level());
            var level = side[i - 1];
            if (data.ContainsKey($"{prefix}p{i}")) level.Price = PyFloat(data[$"{prefix}p{i}"]);
            if (data.ContainsKey($"{prefix}q{i}")) level.Quantity = PyInt(data[$"{prefix}q{i}"]);
            if (data.ContainsKey($"{prefix}o{i}")) level.Orders = PyInt(data[$"{prefix}o{i}"]);
        }
    }
}
