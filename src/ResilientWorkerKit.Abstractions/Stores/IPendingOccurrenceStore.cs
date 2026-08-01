namespace ResilientWorkerKit;

/// <summary>
/// Durable queue of planned occurrences. Unlike a schedule, which is a pure function of time,
/// entries here are facts that must survive a restart — a follow-up retry queued five minutes
/// out is worthless if a deployment in between erases it.
/// <para>
/// <see cref="TryClaimAsync"/> must be atomic: an occurrence may be claimed by exactly one
/// caller, so a future multi-instance deployment cannot run the same planned action twice.
/// </para>
/// </summary>
public interface IPendingOccurrenceStore
{
    /// <summary>Queues an occurrence.</summary>
    Task AddAsync(PendingOccurrence occurrence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the earliest unclaimed occurrence for the job, whether or not it is due yet, so
    /// the scheduler can wait for it. Returns null when the queue is empty for that job.
    /// </summary>
    Task<PendingOccurrence?> GetNextAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically removes the occurrence and reports whether this caller won it. A caller that
    /// receives <see langword="false"/> must not run the occurrence.
    /// </summary>
    Task<bool> TryClaimAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Number of queued occurrences for the job (diagnostics and health).</summary>
    Task<int> CountAsync(string jobId, CancellationToken cancellationToken = default);
}
