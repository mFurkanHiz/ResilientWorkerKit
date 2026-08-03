namespace ResilientWorkerKit;

/// <summary>
/// Durable queue of planned occurrences. Unlike a schedule, which is a pure function of time,
/// entries here are facts that must survive a restart — a follow-up retry queued five minutes
/// out is worthless if a deployment in between erases it.
/// <para>
/// Occurrences are executed under a <b>lease</b>, not removed on claim: a claim that deleted
/// the row (as in 1.x) left a window between the delete and the first durable execution record
/// in which a crash lost the planned action permanently. A lease is revocable — if the process
/// dies, the lease expires and the occurrence becomes acquirable again — so the row is only
/// deleted by <see cref="CompleteAsync"/>, after an execution outcome exists durably.
/// </para>
/// <para>
/// Lease operations must be atomic at the database (single winner across processes) and
/// token-checked: only the holder of the lease token may renew, complete or release. This
/// makes the pending-occurrence capability itself safe for multiple host instances; it does
/// <b>not</b> make the engine as a whole multi-instance safe (see docs/limitations.md).
/// </para>
/// <para>
/// All <c>nowUtc</c> parameters are supplied by the caller (from <see cref="TimeProvider"/>);
/// implementations never read a clock of their own, so behaviour is deterministic under test
/// and consistent within a host.
/// </para>
/// </summary>
public interface IPendingOccurrenceStore
{
    /// <summary>
    /// Queues an occurrence. Returns <see langword="false"/> when an occurrence with the same
    /// (<see cref="PendingOccurrence.JobId"/>, <see cref="PendingOccurrence.IdentityToken"/>)
    /// is already queued — the database is the arbiter, so two processes planning the same
    /// logical occurrence cannot double-queue it.
    /// </summary>
    Task<bool> AddAsync(PendingOccurrence occurrence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the occurrence with the earliest <em>effective</em> time for the job, or null
    /// when the queue is empty for that job. The effective time of an unleased (or
    /// expired-lease) occurrence is its due time; an occurrence under an unexpired lease is
    /// surfaced no earlier than its lease expiry, so a scheduler can sleep until another
    /// owner's lease would expire instead of polling.
    /// </summary>
    Task<PendingOccurrence?> GetNextAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically leases the occurrence for <paramref name="duration"/> and returns the lease
    /// token, or null when the occurrence is gone or already leased with an unexpired lease.
    /// Exactly one concurrent caller can win; the database decides.
    /// </summary>
    Task<string?> TryAcquireLeaseAsync(
        string id, string owner, TimeSpan duration, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease to <paramref name="nowUtc"/> + <paramref name="duration"/>. Returns
    /// <see langword="false"/> when the caller no longer holds the lease (wrong token, or the
    /// row is gone) — the caller should assume another owner may now run the occurrence.
    /// </summary>
    Task<bool> TryRenewLeaseAsync(
        string id, string leaseToken, TimeSpan duration, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the occurrence. Valid only for the lease holder; called after the execution
    /// outcome is durably recorded (and any follow-up is queued). Returns
    /// <see langword="false"/> when the caller did not hold the lease.
    /// </summary>
    Task<bool> CompleteAsync(string id, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the lease so the occurrence is immediately acquirable again — used when the
    /// holder decides not to run it after all (for example, the job lock was unavailable).
    /// Valid only for the lease holder. Returns <see langword="false"/> when the caller did
    /// not hold the lease.
    /// </summary>
    Task<bool> ReleaseAsync(string id, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>Number of queued occurrences for the job, leased or not (diagnostics and health).</summary>
    Task<int> CountAsync(string jobId, CancellationToken cancellationToken = default);
}
