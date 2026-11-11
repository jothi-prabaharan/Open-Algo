using System.Collections.Concurrent;
using System.Globalization;

namespace EPTradeBot.Infrastructure.Security;

/// <summary>
/// The state that travels to a broker's sign-in page and back, so a sign-in is completed only for one this server started, for this account,
/// within ten minutes, and once. It is the expiry sealed with the account's token protector, so nothing is stored: a restart in the middle of a
/// sign-in does not lose it, and a state issued for one account opens nothing on another. The broker's request token is single-use too.
/// </summary>
public sealed class LoginStates(TokenProtector protector, TimeProvider clock)
{
    private const string Field = "login-state";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, long> used = new(StringComparer.Ordinal);

    public string Issue(Guid accountId) =>
        protector.Protect(accountId, Field, clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>True once for a state issued for this account that has not expired.</summary>
    public bool Consume(Guid accountId, string? state)
    {
        if (string.IsNullOrEmpty(state) || protector.Unprotect(accountId, Field, state) is not { } sealedExpiry) return false;
        if (!long.TryParse(sealedExpiry, NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)) return false;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (expiry < now) return false;
        foreach (var (key, until) in used) if (until < now) used.TryRemove(key, out _);
        return used.TryAdd(state, expiry);
    }
}
