namespace ResilientWorkerKit;

/// <summary>
/// The complete, validated configuration of one registered job. Built by the registration API
/// and treated as immutable afterwards.
/// </summary>
public sealed class JobDefinition
{
    /// <summary>Stable job identity (unique within the host).</summary>
    public required string JobId { get; init; }

    /// <summary>Human-readable name; defaults to the job id.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The job implementation type (implements <see cref="IWorkerJob"/>).</summary>
    public required Type JobType { get; init; }

    /// <summary>Whether the job participates in scheduling.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The schedule; null when the job only runs on startup or manually.</summary>
    public IJobSchedule? Schedule { get; init; }

    /// <summary>Run one occurrence immediately when the host starts.</summary>
    public bool RunOnStartup { get; init; }

    /// <summary>The job's time zone (UTC when not configured).</summary>
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Total execution timeout across all attempts; null = unlimited.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Retry policy for transient failures, applied as attempts inside one execution.</summary>
    public required JobRetryOptions Retry { get; init; }

    /// <summary>
    /// Optional durable retry policy applied after an execution has failed for good: the engine
    /// queues a follow-up occurrence that survives a restart. Null disables follow-up retries.
    /// </summary>
    public FollowUpRetryOptions? FollowUpRetry { get; init; }

    /// <summary>Behavior when a new occurrence fires while the previous execution still runs.</summary>
    public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.SkipNewExecution;

    /// <summary>Behavior for missed occurrences.</summary>
    public MisfirePolicy MisfirePolicy { get; init; } = MisfirePolicy.Skip;

    /// <summary>Maximum lateness for <see cref="ResilientWorkerKit.MisfirePolicy.RunIfWithinTolerance"/>.</summary>
    public TimeSpan? MisfireTolerance { get; init; }

    /// <summary>
    /// Write an execution-level dead letter whenever an execution ends in
    /// <see cref="JobExecutionStatus.Failed"/> — whether the retries were exhausted or the
    /// failure was permanent from the first attempt.
    /// </summary>
    public bool DeadLetterOnFailure { get; init; }

    /// <summary>Time-to-live for idempotency records created by this job; null = no expiry.</summary>
    public TimeSpan? IdempotencyTimeToLive { get; init; }

    /// <summary>Health evaluation thresholds.</summary>
    public required JobHealthThresholds HealthThresholds { get; init; }
}
