using System.Collections.Concurrent;

namespace ResilientWorkerKit.Stores;

/// <summary>
/// In-memory idempotency store for tests and demos. Records are lost on process exit —
/// <b>not suitable for production</b>; use the EF Core store, whose unique index settles
/// concurrent acquisitions at the database.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<(string JobId, string Key), IdempotencyRecord> _records = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the store.</summary>
    public InMemoryIdempotencyStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<IdempotencyAcquireResult> TryAcquireAsync(string jobId, string key, string executionId, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var fresh = new IdempotencyRecord
        {
            JobId = jobId,
            Key = key,
            Status = IdempotencyStatus.Pending,
            ExecutionId = executionId,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_records.TryAdd((jobId, key), fresh))
            {
                return Task.FromResult(IdempotencyAcquireResult.Acquired);
            }

            if (!_records.TryGetValue((jobId, key), out var existing))
            {
                continue; // removed concurrently; retry the add
            }

            var expired = existing.ExpiresAtUtc is { } expiry && expiry <= now;
            if (!expired && existing.Status == IdempotencyStatus.Completed)
            {
                return Task.FromResult(IdempotencyAcquireResult.AlreadyCompleted);
            }

            if (!expired && existing.Status == IdempotencyStatus.Pending)
            {
                return Task.FromResult(existing.ExecutionId == executionId
                    ? IdempotencyAcquireResult.Acquired
                    : IdempotencyAcquireResult.InProgressElsewhere);
            }

            // Failed or expired: atomically replace; loser of the race re-evaluates.
            if (_records.TryUpdate((jobId, key), fresh, existing))
            {
                return Task.FromResult(IdempotencyAcquireResult.Acquired);
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> ExistsCompletedAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var exists = _records.TryGetValue((jobId, key), out var record)
            && record.Status == IdempotencyStatus.Completed
            && (record.ExpiresAtUtc is not { } expiry || expiry > now);
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task MarkCompletedAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue((jobId, key), out var record))
        {
            record.Status = IdempotencyStatus.Completed;
            record.CompletedAtUtc = _timeProvider.GetUtcNow();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkFailedAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue((jobId, key), out var record))
        {
            record.Status = IdempotencyStatus.Failed;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IdempotencyRecord?> GetAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        _records.TryGetValue((jobId, key), out var record);
        return Task.FromResult(record);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        _records.TryRemove((jobId, key), out _);
        return Task.CompletedTask;
    }
}
