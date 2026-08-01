namespace ResilientWorkerKit;

/// <summary>Persists one checkpoint per job. Writes must be atomic per job.</summary>
public interface IJobCheckpointStore
{
    /// <summary>Returns the job's checkpoint, or null when none was ever saved.</summary>
    Task<JobCheckpoint?> GetAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the job's checkpoint.</summary>
    Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Deletes the job's checkpoint, if present.</summary>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken = default);
}
