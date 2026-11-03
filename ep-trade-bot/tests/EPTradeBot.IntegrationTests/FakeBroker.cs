using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace EPTradeBot.IntegrationTests;

/// <summary>One request a plugin sent, in the same shape the Python recorder writes.</summary>
public sealed record SentRequest(string Method, string Host, string Path, List<KeyValuePair<string, string>> Query, Dictionary<string, string> Headers, List<KeyValuePair<string, string>>? Form, JsonNode? Json)
{
    /// <summary>The query exactly as sent, with its leading "?" ("" when there is none).</summary>
    public string RawQuery { get; init; } = "";

    /// <summary>The body exactly as sent.</summary>
    public string RawBody { get; init; } = "";
}

/// <summary>
/// Stands in for the broker's HTTP API: answers from canned replies keyed by
/// "METHOD /path" (as the Python recorder does) and records every request.
/// A request with no canned reply gets 599, which no broker sends.
/// </summary>
public sealed class FakeBroker : HttpMessageHandler
{
    private readonly object gate = new();
    private Dictionary<string, List<(int Status, string Body)>> canned = new();
    private readonly Dictionary<string, int> calls = new();
    private readonly List<SentRequest> sent = [];

    public void Reset(IEnumerable<KeyValuePair<string, (int Status, string Body)>> replies) =>
        Reset(replies.Select(kv => new KeyValuePair<string, List<(int, string)>>(kv.Key, [kv.Value])));

    /// <summary>Successive calls to one endpoint get successive replies; the last one repeats.</summary>
    public void Reset(IEnumerable<KeyValuePair<string, List<(int Status, string Body)>>> replies)
    {
        lock (gate)
        {
            canned = new Dictionary<string, List<(int, string)>>(replies);
            calls.Clear();
            sent.Clear();
        }
    }

    /// <summary>Forgets what was sent, keeping the replies.</summary>
    public void ClearSent()
    {
        lock (gate) sent.Clear();
    }

    public IReadOnlyList<SentRequest> Sent
    {
        get
        {
            lock (gate) return sent.ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = new List<KeyValuePair<string, string>>();
        var parsed = HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in parsed.AllKeys)
            foreach (var value in parsed.GetValues(key) ?? [])
                query.Add(new(key ?? "", value));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        List<KeyValuePair<string, string>>? form = null;
        JsonNode? json = null;
        var raw = "";
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            var text = await request.Content.ReadAsStringAsync(cancellationToken);
            raw = text;
            var type = request.Content.Headers.ContentType?.MediaType ?? "";
            if (type == "application/x-www-form-urlencoded")
            {
                form = [];
                foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var i = pair.IndexOf('=');
                    form.Add(new(WebUtility.UrlDecode(i < 0 ? pair : pair[..i]), WebUtility.UrlDecode(i < 0 ? "" : pair[(i + 1)..])));
                }
            }
            else if (type.Contains("json") && text.Length > 0)
            {
                json = JsonNode.Parse(text);
            }
        }

        (int Status, string Body) reply;
        lock (gate)
        {
            sent.Add(new SentRequest(request.Method.Method, uri.Authority, uri.AbsolutePath, query, headers, form, json) { RawQuery = uri.Query, RawBody = raw });
            var key = $"{request.Method.Method} {uri.AbsolutePath}";
            if (canned.TryGetValue(key, out var list) && list.Count > 0)
            {
                var index = calls.GetValueOrDefault(key);
                calls[key] = index + 1;
                reply = list[Math.Min(index, list.Count - 1)];
            }
            else
            {
                reply = (599, $$"""{"status":"error","message":"no canned reply for {{key}}"}""");
            }
        }

        return new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
    }
}
