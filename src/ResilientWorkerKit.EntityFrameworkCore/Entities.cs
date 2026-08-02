namespace ResilientWorkerKit.EntityFrameworkCore;

// Timestamps are persisted as UTC DateTime rather than DateTimeOffset on purpose: SQLite
// cannot ORDER BY or compare DateTimeOffset columns, and every timestamp in the model is
// UTC by construction, so the offset carries no information. The store converts at the
// mapping boundary, keeping DateTimeOffset in the public abstractions.

/// <summary>Relational row for one job execution.</summary>
public sealed class JobExecutionEntity
{
    /// <summary>Primary key.</summary>
    public string ExecutionId { get; set; } = null!;

    /// <summary>The owning job.</summary>
    public string JobId { get; set; } = null!;

    /// <summary>Occurrence identity.</summary>
    public string ScheduledExecutionId { get; set; } = null!;

    /// <summary>Planned time (UTC).</summary>
    public DateTime ScheduledAtUtc { get; set; }

    /// <summary>Planned time in the job's zone.</summary>
    public DateTime? ScheduledLocalTime { get; set; }

    /// <summary>Time zone id.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Trigger type (schedule/startup/misfire/queued-overlap/manual).</summary>
    public string TriggerType { get; set; } = "schedule";

    /// <summary>Start time (UTC).</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Completion time (UTC).</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Status (stored as string).</summary>
    public JobExecutionStatus Status { get; set; }

    /// <summary>Failure classification (stored as string).</summary>
    public JobFailureKind? FailureKind { get; set; }

    /// <summary>Attempts performed.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Total duration (ms).</summary>
    public double? DurationMs { get; set; }

    /// <summary>Exception type name.</summary>
    public string? ErrorType { get; set; }

    /// <summary>Sanitized message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Sanitized detail (stack trace).</summary>
    public string? ErrorDetail { get; set; }

    /// <summary>Correlation id.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Host instance id.</summary>
    public string? HostInstanceId { get; set; }

    /// <summary>Last checkpoint summary.</summary>
    public string? LastCheckpointSummary { get; set; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Row update time (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Relational row for a job's checkpoint (one per job).</summary>
public sealed class JobCheckpointEntity
{
    /// <summary>Primary key: the job id.</summary>
    public string JobId { get; set; } = null!;

    /// <summary>JSON payload.</summary>
    public string PayloadJson { get; set; } = null!;

    /// <summary>Payload type name (diagnostics).</summary>
    public string? PayloadType { get; set; }

    /// <summary>Last advance time (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Relational row for one idempotency record; PK (JobId, Key) settles acquire races.</summary>
public sealed class JobIdempotencyEntity
{
    /// <summary>The owning job (part of the primary key).</summary>
    public string JobId { get; set; } = null!;

    /// <summary>The idempotency key (part of the primary key).</summary>
    public string Key { get; set; } = null!;

    /// <summary>Status (stored as string).</summary>
    public IdempotencyStatus Status { get; set; }

    /// <summary>The acquiring execution.</summary>
    public string? ExecutionId { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Completion time (UTC).</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Optional expiry (UTC).</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Optimistic-concurrency token: incremented on every state change.</summary>
    public int Version { get; set; }
}

/// <summary>Relational row for one durably planned occurrence (today: a follow-up retry).</summary>
public sealed class JobPendingOccurrenceEntity
{
    /// <summary>Primary key.</summary>
    public string Id { get; set; } = null!;

    /// <summary>The job to run.</summary>
    public string JobId { get; set; } = null!;

    /// <summary>When the occurrence becomes due (UTC).</summary>
    public DateTime DueAtUtc { get; set; }

    /// <summary>Occurrence identity token.</summary>
    public string IdentityToken { get; set; } = null!;

    /// <summary>What planned it (<c>follow-up-retry</c>).</summary>
    public string Source { get; set; } = null!;

    /// <summary>The occurrence being followed up on.</summary>
    public string? OriginScheduledExecutionId { get; set; }

    /// <summary>1-based follow-up ordinal.</summary>
    public int FollowUpOrdinal { get; set; }

    /// <summary>Optional payload; unused by follow-up retries.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Host currently holding the lease; null when unleased.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>
    /// Proof of lease ownership, known only to the acquirer. Renew, complete and release are
    /// conditional on this value, so a host that lost its lease cannot affect the row.
    /// </summary>
    public string? LeaseToken { get; set; }

    /// <summary>When the current lease was acquired (UTC); null when unleased.</summary>
    public DateTime? ClaimedAtUtc { get; set; }

    /// <summary>
    /// When the current lease expires (UTC); null when unleased. An expired lease makes the
    /// row acquirable again — the crash-recovery guarantee.
    /// </summary>
    public DateTime? LeaseExpiresAtUtc { get; set; }
}

/// <summary>Relational row for one dead-letter record.</summary>
public sealed class JobDeadLetterEntity
{
    /// <summary>Primary key.</summary>
    public string Id { get; set; } = null!;

    /// <summary>The owning job.</summary>
    public string JobId { get; set; } = null!;

    /// <summary>The execution that produced the record.</summary>
    public string ExecutionId { get; set; } = null!;

    /// <summary>execution | item.</summary>
    public string Scope { get; set; } = "execution";

    /// <summary>Safe item identifier.</summary>
    public string? ItemId { get; set; }

    /// <summary>Failure classification (stored as string).</summary>
    public JobFailureKind? FailureKind { get; set; }

    /// <summary>Sanitized reason.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>Attempts before dead-lettering.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Masked payload summary.</summary>
    public string? PayloadSummary { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Reprocess time (UTC), when handled.</summary>
    public DateTime? ReprocessedAtUtc { get; set; }
}
