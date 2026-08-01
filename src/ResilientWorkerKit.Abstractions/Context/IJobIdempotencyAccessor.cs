namespace ResilientWorkerKit;

/// <summary>
/// Idempotency operations for the current job, bound to the current execution and the job's
/// configured record time-to-live. Keys must be stable business identities and must not
/// contain personal data (see docs/idempotency.md).
/// </summary>
public interface IJobIdempotencyAccessor
{
    /// <summary>Returns whether the key already completed (and has not expired).</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomically acquires the key for this execution; exactly one concurrent caller wins.</summary>
    Task<IdempotencyAcquireResult> TryAcquireAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Marks the key completed — call after the side effect durably succeeded.</summary>
    Task MarkCompletedAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Marks the key failed so a later execution may retry it.</summary>
    Task MarkFailedAsync(string key, CancellationToken cancellationToken = default);
}
