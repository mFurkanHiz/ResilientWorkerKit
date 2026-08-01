namespace ResilientWorkerKit;

/// <summary>Outcome of <see cref="IIdempotencyStore.TryAcquireAsync"/>.</summary>
public enum IdempotencyAcquireResult
{
    /// <summary>The key was acquired; the caller owns the side effect and must mark it completed or failed.</summary>
    Acquired = 0,

    /// <summary>The key already completed (and has not expired); the side effect must be skipped.</summary>
    AlreadyCompleted = 1,

    /// <summary>Another execution currently holds the key in <see cref="IdempotencyStatus.Pending"/> state.</summary>
    InProgressElsewhere = 2,
}

/// <summary>
/// Stores idempotency records. <see cref="TryAcquireAsync"/> must be atomic: when two
/// executions race for the same key, exactly one may win (relational implementations enforce
/// this with a unique index, the in-memory implementation with an atomic dictionary insert).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically acquires the key for the given execution. A <see cref="IdempotencyStatus.Failed"/>
    /// or expired record may be re-acquired; a live <see cref="IdempotencyStatus.Completed"/> record
    /// yields <see cref="IdempotencyAcquireResult.AlreadyCompleted"/>; a live pending record owned by
    /// another execution yields <see cref="IdempotencyAcquireResult.InProgressElsewhere"/>.
    /// </summary>
    Task<IdempotencyAcquireResult> TryAcquireAsync(string jobId, string key, string executionId, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Returns whether a live (non-expired) completed record exists for the key.</summary>
    Task<bool> ExistsCompletedAsync(string jobId, string key, CancellationToken cancellationToken = default);

    /// <summary>Marks the key completed.</summary>
    Task MarkCompletedAsync(string jobId, string key, CancellationToken cancellationToken = default);

    /// <summary>Marks the key failed, allowing later re-acquisition.</summary>
    Task MarkFailedAsync(string jobId, string key, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for the key, or null (expired records are still returned).</summary>
    Task<IdempotencyRecord?> GetAsync(string jobId, string key, CancellationToken cancellationToken = default);

    /// <summary>Removes the record for the key, if present.</summary>
    Task RemoveAsync(string jobId, string key, CancellationToken cancellationToken = default);
}
