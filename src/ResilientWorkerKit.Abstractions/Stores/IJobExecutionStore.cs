namespace ResilientWorkerKit;

/// <summary>
/// Persists job execution history. Implementations must be safe for concurrent use by
/// multiple executions within one host.
/// </summary>
public interface IJobExecutionStore
{
    /// <summary>Persists a new execution record (status <see cref="JobExecutionStatus.Running"/>).</summary>
    Task CreateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing execution record (matched by <see cref="JobExecutionRecord.ExecutionId"/>).</summary>
    Task UpdateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns the execution with the given id, or null.</summary>
    Task<JobExecutionRecord?> GetAsync(string executionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recently started execution of the job, or null.</summary>
    Task<JobExecutionRecord?> GetLatestAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent executions of the job, newest first.</summary>
    Task<IReadOnlyList<JobExecutionRecord>> GetRecentAsync(string jobId, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether any execution exists for the given schedule occurrence identity.
    /// With <paramref name="completedOnly"/> only <see cref="JobExecutionStatus.Completed"/>
    /// executions count. Used to prevent duplicate occurrence execution across restarts
    /// (monthly identity, one-time schedules, misfire recovery, DST fall-back).
    /// </summary>
    Task<bool> ExistsForScheduledExecutionAsync(string jobId, string scheduledExecutionId, bool completedOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks all records still in <see cref="JobExecutionStatus.Running"/> as
    /// <see cref="JobExecutionStatus.Abandoned"/> and returns how many were updated.
    /// Called during startup recovery: with a single active host instance (the v1 deployment
    /// model), any running record found at startup belongs to a process that died.
    /// </summary>
    Task<int> MarkRunningAsAbandonedAsync(CancellationToken cancellationToken = default);
}
