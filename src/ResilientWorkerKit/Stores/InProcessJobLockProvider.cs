using System.Collections.Concurrent;

namespace ResilientWorkerKit.Stores;

/// <summary>
/// Per-job lock scoped to the current process. Sufficient for the v1 single-instance
/// deployment model; a distributed lock provider is the Phase 2 extension point for
/// multi-instance deployments.
/// </summary>
public sealed class InProcessJobLockProvider : IJobLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(jobId, static _ => new SemaphoreSlim(1, 1));
        var acquired = await semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return acquired ? new LockHandle(semaphore) : null;
    }

    private sealed class LockHandle : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        public LockHandle(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
