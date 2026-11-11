namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// What the service layer needs to know about the key a call came in with: which key, and how that key's orders are handled. Set by the endpoint
/// for the length of the call (and by the Action Center when it replays an approved order, with <see cref="ApprovedBy"/>), so no service signature
/// has to carry it.
/// </summary>
public sealed record ApiCall(Guid? KeyId, string OrderMode, string? ApprovedBy = null)
{
    public bool IsSemiAuto => ApprovedBy is null && string.Equals(OrderMode, "semi_auto", StringComparison.Ordinal);
}

public static class ApiCallContext
{
    private static readonly AsyncLocal<ApiCall?> Current = new();

    public static ApiCall Get() => Current.Value ?? new ApiCall(null, "auto");

    public static IDisposable Begin(ApiCall call)
    {
        var previous = Current.Value;
        Current.Value = call;
        return new Scope(previous);
    }

    private sealed class Scope(ApiCall? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
