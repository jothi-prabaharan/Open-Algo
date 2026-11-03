using System.Collections.Concurrent;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>
/// One async lock per key. The dispatcher holds an account's lock from the risk check
/// to the write, so two orders cannot both pass a limit that only one of them fits under.
/// </summary>
public sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
