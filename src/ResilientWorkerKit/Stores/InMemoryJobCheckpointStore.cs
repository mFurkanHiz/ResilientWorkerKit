using System.Collections.Concurrent;

namespace ResilientWorkerKit.Stores;

/// <summary>
/// In-memory checkpoint store for tests and demos. State is lost on process exit —
/// <b>not suitable for production</b>; use the EF Core store for durable checkpoints.
/// </summary>
public sealed class InMemoryJobCheckpointStore : IJobCheckpointStore
{
    private readonly ConcurrentDictionary<string, JobCheckpoint> _checkpoints = new();

    /// <inheritdoc />
    public Task<JobCheckpoint?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        _checkpoints.TryGetValue(jobId, out var checkpoint);
        return Task.FromResult(checkpoint);
    }

    /// <inheritdoc />
    public Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _checkpoints[checkpoint.JobId] = checkpoint;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        _checkpoints.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }
}
