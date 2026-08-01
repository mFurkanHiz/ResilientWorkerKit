namespace ResilientWorkerKit;

/// <summary>Per-job thresholds used by health evaluation (see docs/health-checks.md).</summary>
public sealed class JobHealthThresholds
{
    /// <summary>Consecutive failures after which the job reports Degraded. Default 2.</summary>
    public int DegradedAfterConsecutiveFailures { get; set; } = 2;

    /// <summary>Consecutive failures after which the job reports Unhealthy. Default 5.</summary>
    public int UnhealthyAfterConsecutiveFailures { get; set; } = 5;

    /// <summary>
    /// Optional: the job reports Unhealthy when it has run at least once but has had no
    /// successful execution for this long.
    /// </summary>
    public TimeSpan? UnhealthyWhenNoSuccessFor { get; set; }

    /// <summary>
    /// Optional: a running execution longer than this is considered stuck (job reports
    /// Degraded). When null, the engine falls back to 2× the job's total timeout, if one is set.
    /// </summary>
    public TimeSpan? StuckAfter { get; set; }
}
