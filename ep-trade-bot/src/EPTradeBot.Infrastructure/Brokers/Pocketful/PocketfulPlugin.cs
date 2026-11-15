using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Pocketful;

/// <summary>
/// Pocketful (broker/pocketful): orders, books and funds on trade.pocketful.in with the access token as a bearer token. Nearly every call needs the client id, which the sign-in gives the session, and which is otherwise asked for
/// (GET /api/v1/user/trading_info) at the start of the call, as OpenAlgo does. A market order is a native market order: OpenAlgo's market price protection is only applied when an auth token is passed to its transform, and the order path does not pass one.
/// </summary>
[BrokerPlugin]
public sealed partial class PocketfulPlugin(IHttpClientFactory http, BrokerSessionFactory sessions, ISymbolMaster symbols, TimeProvider clock, IPocketfulSockets? sockets = null) : IBrokerOrders, IBrokerPortfolio, IBrokerFunds
{
    private const string DefaultBaseUrl = "https://trade.pocketful.in";
    private const string Display = "Pocketful";

    public string Name => "pocketful";
    public string DisplayName => "Pocketful";
    public BrokerCapability Capabilities => BrokerCapability.Orders | BrokerCapability.Portfolio | BrokerCapability.Funds | BrokerCapability.Quotes | BrokerCapability.Depth;
    public IReadOnlyList<string> RequiredKeys { get; } = ["ApiKey", "ApiSecret"];

    private static IPocketfulSockets? defaultSockets;

    private IPocketfulSockets Sockets => sockets ?? (defaultSockets ??= new PocketfulSnapshotSocket());

    private HttpClient Client() => http.CreateClient(Name);

    private DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>One call: the status and the body as read (null when it was not JSON). A call that cannot be made at all is a 502.</summary>
    private async Task<(int Status, JsonNode? Body, string Text)> SendAsync(BrokerSession session, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? query, JsonNode? body, CancellationToken cancellationToken)
    {
        var url = sessions.BaseUrl(Name, DefaultBaseUrl) + path;
        if (query is not null)
        {
            var parts = string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
            if (parts.Length > 0) url += "?" + parts;
        }

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return ((int)response.StatusCode, parsed, text);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"{Display} did not answer. Check your connection, then try again.", 502, ex);
        }
    }

    /// <summary>The message in an answer, when it has one.</summary>
    private static string? Reason(JsonNode? body) => body is JsonObject o && Truthy(o["message"]) ? Str(o["message"]) : null;

    private static bool Succeeded(JsonNode? body) => body is JsonObject o && Str(o["status"]) == "success";

    /// <summary>The client id: the sign-in's when the session has one, else asked for.</summary>
    private async Task<string> ClientIdAsync(BrokerSession session, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(session.ClientId)) return session.ClientId;
        var (_, body, _) = await SendAsync(session, HttpMethod.Get, "/api/v1/user/trading_info", null, null, cancellationToken);
        if (Succeeded(body) && (body as JsonObject)?["data"] is JsonObject data && Truthy(data["client_id"])) return Str(data["client_id"])!;
        throw new BrokerException($"{Display} did not say which client this session belongs to ({Reason(body) ?? "no reason was given"}). Sign in to {Display} again, then try again.", 502);
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    private static string OrderType(string priceType) => priceType.ToUpperInvariant() switch { "MARKET" => "MARKET", "LIMIT" => "LIMIT", "SL" => "SL", "SL-M" => "SLM", _ => "MARKET" };

    private static string Product(string product) => product.ToUpperInvariant() switch { "CNC" => "CNC", "NRML" => "NRML", "MIS" => "MIS", _ => "MIS" };

    public async Task<BrokerCallResult> PlaceAsync(BrokerSession session, BrokerOrder order, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, order.Symbol, order.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{order.Symbol} on {order.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        string clientId;
        try
        {
            clientId = await ClientIdAsync(session, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
        }

        var payload = new JsonObject
        {
            ["exchange"] = order.Exchange, ["instrument_token"] = info.Token, ["client_id"] = clientId, ["order_type"] = OrderType(order.PriceType), ["amo"] = false, ["price"] = (double)order.Price, ["quantity"] = order.Quantity,
            ["disclosed_quantity"] = order.DisclosedQuantity, ["validity"] = "DAY", ["product"] = Product(order.Product), ["order_side"] = order.Action.ToUpperInvariant(), ["device"] = "WEB", ["user_order_id"] = 1,
            ["trigger_price"] = (double)order.TriggerPrice, ["execution_type"] = "REGULAR",
        };
        (int Status, JsonNode? Body, string Text) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/api/v1/orders", null, payload, cancellationToken);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The order was sent to {Display} but no reply came back, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }

        if (reply.Status >= 400) return BrokerCallResult.Refused(reply.Status, Reason(reply.Body) ?? $"{Display} refused the order.");
        if (reply.Body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} could not confirm the order, so it may have been placed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        if (!Succeeded(answer)) return BrokerCallResult.Refused(400, Reason(answer) ?? $"{Display} refused the order.");
        if (answer["data"] is JsonObject data && Truthy(data["oms_order_id"])) return BrokerCallResult.Success(Str(data["oms_order_id"])!);
        return new BrokerCallResult(false, 502, $"{Display} accepted the order but sent no order number, so it cannot be followed. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
    }

    public async Task<BrokerCallResult> ModifyAsync(BrokerSession session, BrokerOrderChange change, CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(Name, change.Symbol, change.Exchange, cancellationToken);
        if (info is null) return BrokerCallResult.Refused(400, $"{change.Symbol} on {change.Exchange} is not in the {Display} master contract. Check the symbol, or download the master contract again.");
        string clientId;
        try
        {
            clientId = await ClientIdAsync(session, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
        }

        var payload = new JsonObject
        {
            ["exchange"] = change.Exchange, ["instrument_token"] = info.Token, ["client_id"] = clientId, ["order_type"] = OrderType(change.PriceType), ["price"] = (double)change.Price, ["quantity"] = change.Quantity,
            ["disclosed_quantity"] = change.DisclosedQuantity, ["validity"] = "DAY", ["product"] = Product(change.Product), ["order_side"] = change.Action.ToUpperInvariant(), ["device"] = "WEB", ["user_order_id"] = 1,
            ["trigger_price"] = (double)change.TriggerPrice, ["oms_order_id"] = change.OrderId, ["execution_type"] = "REGULAR",
        };
        try
        {
            var (status, body, _) = await SendAsync(session, HttpMethod.Put, "/api/v1/orders", null, payload, cancellationToken);
            if (status >= 400) return BrokerCallResult.Refused(status, Reason(body) ?? $"{Display} refused the change.");
            if (body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the change may have been made. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
            if (!Succeeded(answer)) return BrokerCallResult.Refused(400, Reason(answer) ?? "Failed to modify order");
            return BrokerCallResult.Success(answer["data"] is JsonObject data && Truthy(data["oms_order_id"]) ? Str(data["oms_order_id"])! : change.OrderId);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The change was sent to {Display} but no reply came back, so it may have been made. Check the order book on {Display} before trying again.", MayHaveBeenSent: true);
        }
    }

    public async Task<BrokerCallResult> CancelAsync(BrokerSession session, string orderId, CancellationToken cancellationToken)
    {
        string clientId;
        try
        {
            clientId = await ClientIdAsync(session, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return BrokerCallResult.Refused(ex.HttpStatus, ex.Message);
        }

        try
        {
            var (status, body, _) = await SendAsync(session, HttpMethod.Delete, $"/api/v1/orders/{orderId}", [new("client_id", clientId)], null, cancellationToken);
            if (status >= 400) return BrokerCallResult.Refused(status, Reason(body) ?? $"{Display} refused to cancel the order.");
            if (body is not JsonObject answer) return new BrokerCallResult(false, 502, $"{Display} sent an answer that could not be read, so the order may have been cancelled. Check the order book on {Display}.", MayHaveBeenSent: true);
            if (!Succeeded(answer)) return BrokerCallResult.Refused(400, Reason(answer) ?? "Failed to cancel order");
            return BrokerCallResult.Success(answer["data"] is JsonObject data && Truthy(data["oms_order_id"]) ? Str(data["oms_order_id"])! : orderId);
        }
        catch (BrokerException ex) when (ex.HttpStatus == 502)
        {
            return new BrokerCallResult(false, 502, $"The cancel was sent to {Display} but no reply came back, so the order may have been cancelled. Check the order book on {Display}.", MayHaveBeenSent: true);
        }
    }
}
