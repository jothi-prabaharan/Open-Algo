namespace EPTradeBot.Core.Mcp;

/// <summary>A sliding one-minute count of calls per token and kind, so a runaway client is slowed before it can flood a broker. Pure: the time comes in.</summary>
public sealed class McpRateLimiter
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Token, bool Write), Queue<DateTime>> calls = [];

    /// <summary>Counts the call and returns true, or returns false (counting nothing) when the token has used its calls for the last minute.</summary>
    public bool TryTake(string token, bool write, int perMinute, DateTime nowUtc)
    {
        lock (gate)
        {
            if (!calls.TryGetValue((token, write), out var q)) calls[(token, write)] = q = new Queue<DateTime>();
            while (q.Count > 0 && nowUtc - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= perMinute) return false;
            q.Enqueue(nowUtc);
            return true;
        }
    }

    /// <summary>Forgets tokens that have not called for a minute, so the table does not grow with every token ever issued.</summary>
    public void Sweep(DateTime nowUtc)
    {
        lock (gate)
        {
            foreach (var key in calls.Where(kv => kv.Value.Count == 0 || nowUtc - kv.Value.Peek() >= TimeSpan.FromMinutes(1) && nowUtc - kv.Value.Last() >= TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList())
                calls.Remove(key);
        }
    }
}
