namespace ResilientWorkerKit;

/// <summary>
/// Durable history record of one job execution (all retry attempts included).
/// Must never contain secrets, tokens or raw API payloads — only safe metadata.
/// </summary>
public sealed class JobExecutionRecord
{
    /// <summary>The job this execution belongs to.</summary>
    public required string JobId { get; init; }

    /// <summary>Unique id of this execution; stable across its retry attempts.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Identity of the schedule occurrence (<c>jobId:identityToken</c>).</summary>
    public required string ScheduledExecutionId { get; init; }

    /// <summary>Planned execution time (UTC).</summary>
    public required DateTimeOffset ScheduledAtUtc { get; init; }

    /// <summary>Planned execution time in the job's own time zone.</summary>
    public DateTime? ScheduledLocalTime { get; init; }

    /// <summary>IANA/Windows id of the job's time zone.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>How the execution was initiated: <c>schedule</c>, <c>startup</c>, <c>misfire</c>, <c>queued-overlap</c> or <c>manual</c>.</summary>
    public string TriggerType { get; init; } = "schedule";

    /// <summary>Actual start time (UTC).</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Completion time (UTC); null while running.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>Current status.</summary>
    public JobExecutionStatus Status { get; set; } = JobExecutionStatus.Running;

    /// <summary>Failure classification of the final attempt, when the execution did not complete.</summary>
    public JobFailureKind? FailureKind { get; set; }

    /// <summary>Number of attempts performed (1 = no retries).</summary>
    public int AttemptCount { get; set; } = 1;

    /// <summary>Total duration in milliseconds, measured from first attempt start to completion.</summary>
    public double? DurationMs { get; set; }

    /// <summary>Full type name of the final exception, if any.</summary>
    public string? ErrorType { get; set; }

    /// <summary>Sanitized exception message (never raw payloads or secrets).</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Sanitized diagnostic detail (stack trace). Not exposed to end users.</summary>
    public string? ErrorDetail { get; set; }

    /// <summary>Correlation id propagated into logs and outbound HTTP calls.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Identity of the host instance that ran the execution.</summary>
    public string? HostInstanceId { get; init; }

    /// <summary>Short, safe summary of the last checkpoint saved during this execution.</summary>
    public string? LastCheckpointSummary { get; set; }

    /// <summary>Record creation time (UTC).</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Last record update time (UTC).</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
