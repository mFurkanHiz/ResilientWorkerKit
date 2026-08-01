namespace ResilientWorkerKit;

/// <summary>Point-in-time health information for one job (maintained in-memory by the engine).</summary>
public sealed class JobHealthSnapshot
{
    /// <summary>The job id.</summary>
    public required string JobId { get; init; }

    /// <summary>Whether the job is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether an execution is currently running.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Start time of the currently running execution, if any.</summary>
    public DateTimeOffset? RunningSinceUtc { get; init; }

    /// <summary>Scheduled time of the most recent occurrence that was started.</summary>
    public DateTimeOffset? LastScheduledAtUtc { get; init; }

    /// <summary>Start time of the most recent execution.</summary>
    public DateTimeOffset? LastStartedAtUtc { get; init; }

    /// <summary>Completion time of the most recent execution.</summary>
    public DateTimeOffset? LastCompletedAtUtc { get; init; }

    /// <summary>Completion time of the most recent successful execution.</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    /// <summary>Time of the most recent failure.</summary>
    public DateTimeOffset? LastFailureAtUtc { get; init; }

    /// <summary>Status of the most recent finished execution.</summary>
    public JobExecutionStatus? LastResult { get; init; }

    /// <summary>Duration of the most recent finished execution in milliseconds.</summary>
    public double? LastDurationMs { get; init; }

    /// <summary>Number of consecutive failed executions (reset on success).</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Next planned occurrence (UTC), when known.</summary>
    public DateTimeOffset? NextOccurrenceUtc { get; init; }

    /// <summary>Most recent progress note reported by the job.</summary>
    public string? LastProgress { get; init; }

    /// <summary>Short summary of the job's last saved checkpoint.</summary>
    public string? LastCheckpointSummary { get; init; }
}

/// <summary>Read access to per-job health snapshots.</summary>
public interface IJobHealthTracker
{
    /// <summary>Returns the snapshot for one job, or null when the job is unknown.</summary>
    JobHealthSnapshot? Get(string jobId);

    /// <summary>Returns snapshots for all registered jobs.</summary>
    IReadOnlyList<JobHealthSnapshot> GetAll();
}
