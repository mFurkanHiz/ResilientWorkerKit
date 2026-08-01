using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit;

/// <summary>
/// Everything a job needs during one execution attempt. Created by the engine per attempt;
/// <see cref="ExecutionId"/>, <see cref="Items"/> and the accessors are stable across the
/// attempts of one execution, while <see cref="AttemptNumber"/> changes.
/// </summary>
public sealed class JobExecutionContext
{
    /// <summary>Stable job identity.</summary>
    public required string JobId { get; init; }

    /// <summary>Human-readable job name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Unique execution id; stable across retry attempts.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Identity of the schedule occurrence (<c>jobId:identityToken</c>).</summary>
    public required string ScheduledExecutionId { get; init; }

    /// <summary>1-based attempt number.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Planned execution time (UTC).</summary>
    public required DateTimeOffset ScheduledAtUtc { get; init; }

    /// <summary>Planned execution time in the job's time zone, when the schedule is zone-aware.</summary>
    public DateTime? ScheduledLocalTime { get; init; }

    /// <summary>The job's time zone id.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>Actual start of the execution (first attempt), UTC.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Correlation id for logs and outbound calls.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Identity of the current host instance.</summary>
    public required string HostInstanceId { get; init; }

    /// <summary>
    /// Scoped service provider — a fresh dependency-injection scope per execution.
    /// Never the root provider.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Job-scoped logger; log entries carry JobId/ExecutionId/AttemptNumber scope values.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Typed checkpoint access.</summary>
    public required IJobCheckpointAccessor Checkpoints { get; init; }

    /// <summary>Idempotency operations bound to this job and execution.</summary>
    public required IJobIdempotencyAccessor Idempotency { get; init; }

    /// <summary>Item-level dead-letter recording.</summary>
    public required IJobDeadLetterAccessor DeadLetters { get; init; }

    /// <summary>Per-execution scratch storage shared across attempts. Not persisted.</summary>
    public required IDictionary<string, object?> Items { get; init; }

    /// <summary>
    /// The same token passed to <see cref="IWorkerJob.ExecuteAsync"/>: signalled on host
    /// shutdown, manual cancellation or execution timeout.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Optional progress sink (set by the engine; feeds health snapshots).</summary>
    public IJobProgressReporter? ProgressReporter { get; init; }

    /// <summary>
    /// Reports a short, safe progress note (e.g. <c>"page 3/10"</c>). Appears in the job's
    /// health snapshot and debug logs. Must not contain secrets or personal data.
    /// </summary>
    public void ReportProgress(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ProgressReporter?.Report(JobId, ExecutionId, message);
        Logger.LogDebug("Progress: {ProgressMessage}", message);
    }
}
