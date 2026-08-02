namespace ResilientWorkerKit.UnitTests.TestInfrastructure;

/// <summary>
/// Forces every store call to yield before completing.
/// <para>
/// This exists because a whole class of scheduler bug hides behind synchronous stores. The
/// in-memory stores return already-completed tasks, and Microsoft.Data.Sqlite implements its
/// async API over synchronous I/O, so an execution can run start-to-finish inside the call that
/// started it. A loop that only works under those conditions looks correct in tests and stalls
/// against any real database or HTTP call. Wrapping the stores makes "did the provider complete
/// synchronously?" an explicit axis a test can control instead of an accident it inherits.
/// </para>
/// </summary>
internal sealed class YieldingPendingOccurrenceStore : IPendingOccurrenceStore
{
    private readonly IPendingOccurrenceStore _inner;

    public YieldingPendingOccurrenceStore(IPendingOccurrenceStore inner) => _inner = inner;

    public async Task<bool> AddAsync(PendingOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.AddAsync(occurrence, cancellationToken);
    }

    public async Task<PendingOccurrence?> GetNextAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.GetNextAsync(jobId, nowUtc, cancellationToken);
    }

    public async Task<string?> TryAcquireLeaseAsync(
        string id, string owner, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.TryAcquireLeaseAsync(id, owner, duration, nowUtc, cancellationToken);
    }

    public async Task<bool> TryRenewLeaseAsync(
        string id, string leaseToken, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.TryRenewLeaseAsync(id, leaseToken, duration, nowUtc, cancellationToken);
    }

    public async Task<bool> CompleteAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.CompleteAsync(id, leaseToken, cancellationToken);
    }

    public async Task<bool> ReleaseAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.ReleaseAsync(id, leaseToken, cancellationToken);
    }

    public async Task<int> CountAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.CountAsync(jobId, cancellationToken);
    }
}

/// <summary>Same idea for execution history, which the loop reads on every iteration.</summary>
internal sealed class YieldingJobExecutionStore : IJobExecutionStore
{
    private readonly IJobExecutionStore _inner;

    public YieldingJobExecutionStore(IJobExecutionStore inner) => _inner = inner;

    public async Task CreateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        await _inner.CreateAsync(record, cancellationToken);
    }

    public async Task UpdateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        await _inner.UpdateAsync(record, cancellationToken);
    }

    public async Task<JobExecutionRecord?> GetAsync(string executionId, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.GetAsync(executionId, cancellationToken);
    }

    public async Task<JobExecutionRecord?> GetLatestAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.GetLatestAsync(jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<JobExecutionRecord>> GetRecentAsync(string jobId, int count, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.GetRecentAsync(jobId, count, cancellationToken);
    }

    public async Task<bool> ExistsForScheduledExecutionAsync(string jobId, string scheduledExecutionId, bool completedOnly, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.ExistsForScheduledExecutionAsync(jobId, scheduledExecutionId, completedOnly, cancellationToken);
    }

    public async Task<int> MarkRunningAsAbandonedAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return await _inner.MarkRunningAsAbandonedAsync(cancellationToken);
    }
}
