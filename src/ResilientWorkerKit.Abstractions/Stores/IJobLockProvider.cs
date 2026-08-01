namespace ResilientWorkerKit;

/// <summary>
/// Provides per-job execution locks used to enforce overlap policies. The default
/// implementation is in-process; a distributed implementation (database lock) is a
/// planned Phase 2 extension point for multi-instance deployments.
/// </summary>
public interface IJobLockProvider
{
    /// <summary>
    /// Attempts to acquire the lock for the given job within the timeout.
    /// Returns a handle that releases the lock on disposal, or null when the lock
    /// could not be acquired.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken = default);
}
