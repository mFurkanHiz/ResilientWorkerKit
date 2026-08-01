using System.Collections.Concurrent;

namespace ResilientWorkerKit.Stores;

/// <summary>
/// In-memory dead-letter store for tests and demos. Records are lost on process exit —
/// <b>not suitable for production</b>; use the EF Core store.
/// </summary>
public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentDictionary<string, DeadLetterRecord> _records = new();

    /// <inheritdoc />
    public Task AddAsync(DeadLetterRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records[record.Id] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterRecord>> GetPendingAsync(string? jobId, int maxCount, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DeadLetterRecord> pending = _records.Values
            .Where(r => r.ReprocessedAtUtc is null && (jobId is null || r.JobId == jobId))
            .OrderBy(r => r.CreatedAtUtc)
            .Take(maxCount)
            .ToList();
        return Task.FromResult(pending);
    }

    /// <inheritdoc />
    public Task MarkReprocessedAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue(id, out var record))
        {
            record.ReprocessedAtUtc = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }
}
