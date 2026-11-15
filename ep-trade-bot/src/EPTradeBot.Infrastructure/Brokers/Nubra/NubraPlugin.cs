using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>
/// Nubra V3 (broker/nubra): orders, books and funds on api.nubra.io with the session token as a bearer token and x-device-id OPENALGO on every call. Prices are in paise. A stop order is an ordinary market or limit order with an LTP entry trigger, and a market order
/// is IOC with no price. HTTP 440 means the session has expired. A throttled call (429) is tried again up to three times in all. The keys are the phone number (API key) and the MPIN (API secret), used only by the sign-in.
/// </summary>
[BrokerPlugin]
public sealed partial class NubraPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://api.nubra.io";
    private const string Display = "Nubra";
    private const string DeviceId = "OPENALGO";
    private const int Attempts = 3;
    private const int SessionExpired = 440;
    private const string ExpiredMessage = "Nubra session expired or invalid (HTTP 440). Log in to Nubra again.";

    public string Name => "nubra";
    public string DisplayName => "Nubra";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Margin | BrokerCapability.History;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private HttpClient Client() => http.CreateClient(Name);

    private DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>One call, retried while Nubra answers 429 (up to three tries, 1 and 2 seconds apart). The status, the body as read (null when it was not JSON or was empty) and the text.</summary>
    private async Task<(int Status, JsonNode? Body, string Text)> SendAsync(BrokerSession session, HttpMethod method, string path, JsonNode? body, bool json, CancellationToken cancellationToken)
    {
        var url = sessions.BaseUrl(Name, DefaultBaseUrl) + path;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("x-device-id", DeviceId);
            request.Content = json ? new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json") : null;
            try
            {
                using var response = await Client().SendAsync(request, cancellationToken);
                var status = (int)response.StatusCode;
                if (status == 429 && attempt < Attempts - 1)
                {
                    await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(Math.Pow(2, attempt))), cancellationToken);
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? parsed = null;
                try
                {
                    parsed = JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                }

                return (status, parsed, text);
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
            }
        }
    }

    /// <summary>The reason in a failed answer: its error text, else its message, else nothing.</summary>
    private static string? ErrorOf(JsonNode? body) => body is JsonObject o ? Truthy(o["error"]) ? Str(o["error"]) : Truthy(o["message"]) ? Str(o["message"]) : null : null;

    private static string? MessageOrError(JsonNode? body) => body is JsonObject o ? Truthy(o["message"]) ? Str(o["message"]) : Truthy(o["error"]) ? Str(o["error"]) : null : null;

    /// <summary>A read that must give an object: a status of 400 or more, a 440, a throttle that never cleared, or text that is not JSON is an error that says what to do.</summary>
    private async Task<JsonObject> ReadBookAsync(BrokerSession session, string path, string what, bool json, CancellationToken cancellationToken)
    {
        var (status, body, text) = await SendAsync(session, HttpMethod.Get, path, null, json, cancellationToken);
        if (status == SessionExpired) throw new BrokerException(ExpiredMessage, 401);
        if (status == 429) throw new BrokerException($"{Display} is limiting how fast it is asked for the {what}. Wait a moment, then try again.", 429);
        if (status >= 400) throw new BrokerException(ErrorOf(body) is { } why ? $"{Display} did not give the {what}: {why}" : $"{Display} did not give the {what}. Try again in a moment.", status);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return body as JsonObject ?? throw new BrokerException($"{Display} sent the {what} in a form that could not be read. Try again in a moment.", 502);
    }

    // ---- vocabulary ------------------------------------------------------------------------------------------

    private static string Side(string action) => action.ToUpperInvariant() == "SELL" ? "SELL" : "BUY";

    private static string Delivery(string? product) => (product ?? "").ToUpperInvariant() switch { "CNC" => "CNC", "MIS" => "IDAY", "NRML" => "CNC", _ => "IDAY" };

    private static string ProductOf(string? delivery) => (delivery ?? "").ToUpperInvariant() switch { "CNC" => "CNC", "IDAY" => "MIS", _ => "MIS" };

    private static string PriceType(string pricetype) => pricetype.ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "LIMIT", "SL-M" => "MARKET", _ => "MARKET" };

    private static string Validity(string pricetype) => PriceType(pricetype) == "MARKET" ? "IOC" : "DAY";

    private static long Paise(decimal price) => (long)Math.Round(price * 100, MidpointRounding.ToEven);

    /// <summary>A strategy name as a Nubra tag: runs of anything but letters and digits become one hyphen, the ends are trimmed and the rest is lower case.</summary>
    internal static string Tag(string? strategy)
    {
        var slug = Regex.Replace(string.IsNullOrEmpty(strategy) ? "openalgo" : strategy, "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        return slug.Length == 0 ? "openalgo" : slug;
    }

    private static JsonNode? EntryTrigger(string pricetype, decimal trigger, string action)
    {
        if (pricetype.ToUpperInvariant() is not ("SL" or "SL-M")) return null;
        var paise = Paise(trigger);
        if (paise == 0) return null;
        var bound = Side(action) == "BUY" ? "atOrAbove" : "atOrBelow";
        return new JsonObject { ["triggers"] = new JsonObject { ["ltp"] = new JsonObject { [bound] = new JsonObject { ["value"] = paise } } } };
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        if (info.Token.Length == 0 || !info.Token.All(char.IsAsciiDigit)) return BrokerCallResult.Refused(400, $"No numeric {Display} reference id was found for {order.Symbol} on {order.Exchange}. Download the master contract again.");
        if (order.PriceType.ToUpperInvariant() is "SL" or "SL-M" && order.TriggerPrice == 0)
            return BrokerCallResult.Refused(400, $"{order.PriceType.ToUpperInvariant()} order requires a non-zero trigger_price for {order.Symbol} on {order.Exchange}.");

        var item = new JsonObject
        {
            ["refId"] = long.Parse(info.Token, CultureInfo.InvariantCulture), ["qty"] = order.Quantity, ["side"] = Side(order.Action), ["deliveryType"] = Delivery(order.Product), ["priceType"] = PriceType(order.PriceType),
            ["validityType"] = Validity(order.PriceType), ["isMultiLeg"] = false, ["executionMode"] = "ENTRY", ["stratTags"] = new JsonArray(Tag(order.Strategy)),
        };
        if (PriceType(order.PriceType) == "LIMIT") item["entryPrice"] = Paise(order.Price);
        if (EntryTrigger(order.PriceType, order.TriggerPrice, order.Action) is { } entry) item["entryConfig"] = entry;

        (int Status, JsonNode? Body, string Text) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/sentinel/orders/create", new JsonObject { ["orders"] = new JsonArray(item) }, true, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Status == SessionExpired) return BrokerCallResult.Refused(SessionExpired, ExpiredMessage);
        if (reply.Status == 429) return BrokerCallResult.Refused(429, $"{Display} is limiting how fast orders can be sent, and this one was not placed. Wait a moment, then try again.");
        if (reply.Body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the order may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (reply.Status is 200 or 201)
        {
            if (answer["orders"] is JsonArray { Count: > 0 } orders && orders[0] is JsonObject first && Truthy(first["intentOrderId"])) return BrokerCallResult.Success(Str(first["intentOrderId"]) ?? "");
            return ErrorOf(answer) is { } why ? BrokerCallResult.Refused(400, why)
                : new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        return BrokerCallResult.Refused(reply.Status, MessageOrError(answer) ?? $"{Display} refused the order.");
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        if (!long.TryParse(change.OrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return BrokerCallResult.Refused(400, $"Invalid order id: {change.OrderId}");
        var item = new JsonObject
        {
            ["orderId"] = id, ["qty"] = change.Quantity, ["deliveryType"] = Delivery(change.Product), ["priceType"] = PriceType(change.PriceType), ["validityType"] = Validity(change.PriceType), ["executionMode"] = "ENTRY",
        };
        if (PriceType(change.PriceType) == "LIMIT") item["entryPrice"] = Paise(change.Price);
        if (EntryTrigger(change.PriceType, change.TriggerPrice, change.Action) is { } entry) item["entryConfig"] = entry;
        try
        {
            var (status, body, text) = await SendAsync(session, HttpMethod.Post, "/sentinel/orders/modify", new JsonObject { ["orders"] = new JsonArray(item) }, true, cancellationToken);
            if (status == SessionExpired) return BrokerCallResult.Refused(SessionExpired, ExpiredMessage);
            if (status == 429) return BrokerCallResult.Refused(429, $"{Display} is limiting how fast changes can be sent, and this one was not made. Wait a moment, then try again.");
            if (string.IsNullOrWhiteSpace(text)) return status is 200 or 201 or 204 ? BrokerCallResult.Success(change.OrderId) : BrokerCallResult.Refused(status, $"{Display} answered with nothing. Check the order book on {Display}.");
            if (body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the change may have been made. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
            if (status is 200 or 201)
                return BrokerCallResult.Success(answer["orders"] is JsonArray { Count: > 0 } orders && orders[0] is JsonObject first && Truthy(first["intentOrderId"]) ? Str(first["intentOrderId"])! : change.OrderId);
            return BrokerCallResult.Refused(status, MessageOrError(answer) ?? $"{Display} refused the change.");
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The change was sent to {Display} but no reply came back, so it may have been made. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(orderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return BrokerCallResult.Refused(400, $"Invalid order id: {orderId}");
        try
        {
            var (status, body, text) = await SendAsync(session, HttpMethod.Post, "/sentinel/orders/cancel", new JsonObject { ["orders"] = new JsonArray(new JsonObject { ["orderId"] = id }) }, true, cancellationToken);
            if (status == SessionExpired) return BrokerCallResult.Refused(SessionExpired, ExpiredMessage);
            if (status == 429) return BrokerCallResult.Refused(429, $"{Display} is limiting how fast orders can be cancelled, and this one was not. Wait a moment, then try again.");
            if (string.IsNullOrWhiteSpace(text)) return status is 200 or 201 or 204 ? BrokerCallResult.Success(orderId) : BrokerCallResult.Refused(status, $"{Display} answered with nothing. Check the order book on {Display}.");
            if (body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the order may have been cancelled. Check the order book on {Display}.", MayHaveBeenSent: true);
            return status is 200 or 201 or 204 ? BrokerCallResult.Success(orderId) : BrokerCallResult.Refused(status, MessageOrError(answer) ?? $"{Display} refused to cancel the order.");
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The cancel was sent to {Display} but no reply came back, so the order may have been cancelled. Check the order book on {Display}.", MayHaveBeenSent: true);
        }
    }
}
