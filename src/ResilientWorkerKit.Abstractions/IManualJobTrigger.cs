namespace ResilientWorkerKit;

/// <summary>
/// Extension point for triggering a job on demand (admin endpoints, tests, ops tooling).
/// The triggered execution runs through the normal pipeline: overlap policy, retry,
/// execution history, health tracking.
/// </summary>
public interface IManualJobTrigger
{
    /// <summary>
    /// Requests one immediate execution of the job and returns the new ExecutionId.
    /// Throws <see cref="JobConfigurationException"/> for unknown or disabled jobs.
    /// </summary>
    Task<string> TriggerAsync(string jobId, CancellationToken cancellationToken = default);
}
