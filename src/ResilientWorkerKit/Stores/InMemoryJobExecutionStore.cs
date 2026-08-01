using System.Collections.Concurrent;

namespace ResilientWorkerKit.Stores;

/// <summary>
/// In-memory execution history for tests and demos. History is lost on process exit —
/// <b>not suitable for production</b>; use the EF Core store for durable history.
/// </summary>
public sealed class InMemoryJobExecutionStore : IJobExecutionStore
{
    private readonly ConcurrentDictionary<string, JobExecutionRecord> _byExecutionId = new();

    /// <inheritdoc />
    public Task CreateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _byExecutionId[record.ExecutionId] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _byExecutionId[record.ExecutionId] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<JobExecutionRecord?> GetAsync(string executionId, CancellationToken cancellationToken = default)
    {
        _byExecutionId.TryGetValue(executionId, out var record);
        return Task.FromResult(record);
    }

    /// <inheritdoc />
    public Task<JobExecutionRecord?> GetLatestAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var latest = _byExecutionId.Values
            .Where(r => r.JobId == jobId)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionRecord>> GetRecentAsync(string jobId, int count, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JobExecutionRecord> recent = _byExecutionId.Values
            .Where(r => r.JobId == jobId)
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(count)
            .ToList();
        return Task.FromResult(recent);
    }

    /// <inheritdoc />
    public Task<bool> ExistsForScheduledExecutionAsync(string jobId, string scheduledExecutionId, bool completedOnly, CancellationToken cancellationToken = default)
    {
        var exists = _byExecutionId.Values.Any(r =>
            r.JobId == jobId &&
            r.ScheduledExecutionId == scheduledExecutionId &&
            (!completedOnly || r.Status == JobExecutionStatus.Completed));
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task<int> MarkRunningAsAbandonedAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var record in _byExecutionId.Values)
        {
            if (record.Status == JobExecutionStatus.Running)
            {
                record.Status = JobExecutionStatus.Abandoned;
                record.FailureKind = JobFailureKind.Abandoned;
                count++;
            }
        }

        return Task.FromResult(count);
    }
}
