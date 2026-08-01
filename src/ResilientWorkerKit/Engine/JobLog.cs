using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit.Engine;

/// <summary>
/// Source-generated structured log messages for the engine. Constant templates, exceptions
/// always passed as objects, severities chosen so that routine operation is never noise and
/// real failures are never invisible.
/// </summary>
internal static partial class JobLog
{
    [LoggerMessage(1000, LogLevel.Information, "Job {JobId} registered: schedule={Schedule}, runOnStartup={RunOnStartup}, overlap={OverlapPolicy}, misfire={MisfirePolicy}")]
    public static partial void JobRegistered(ILogger logger, string jobId, string schedule, bool runOnStartup, OverlapPolicy overlapPolicy, MisfirePolicy misfirePolicy);

    [LoggerMessage(1001, LogLevel.Debug, "Next occurrence scheduled at {ScheduledAtUtc:o}")]
    public static partial void JobScheduled(ILogger logger, DateTimeOffset scheduledAtUtc);

    [LoggerMessage(1002, LogLevel.Warning, "Misfire detected: occurrence {ScheduledAtUtc:o} was missed (late by {LateBy}); applying policy {Policy}")]
    public static partial void MisfireDetected(ILogger logger, DateTimeOffset scheduledAtUtc, TimeSpan lateBy, MisfirePolicy policy);

    [LoggerMessage(1003, LogLevel.Information, "Missed occurrence {ScheduledAtUtc:o} skipped per misfire policy")]
    public static partial void MisfireSkipped(ILogger logger, DateTimeOffset scheduledAtUtc);

    [LoggerMessage(1004, LogLevel.Information, "Execution starting (trigger={TriggerType}, scheduledAt={ScheduledAtUtc:o})")]
    public static partial void ExecutionStarting(ILogger logger, string triggerType, DateTimeOffset scheduledAtUtc);

    [LoggerMessage(1005, LogLevel.Debug, "Attempt {AttemptNumber} started")]
    public static partial void AttemptStarted(ILogger logger, int attemptNumber);

    [LoggerMessage(1006, LogLevel.Information, "Execution completed in {DurationMs:F0} ms after {AttemptCount} attempt(s)")]
    public static partial void ExecutionCompleted(ILogger logger, double durationMs, int attemptCount);

    [LoggerMessage(1007, LogLevel.Error, "Execution failed ({FailureKind}) after {AttemptCount} attempt(s) in {DurationMs:F0} ms")]
    public static partial void ExecutionFailed(ILogger logger, Exception exception, JobFailureKind failureKind, int attemptCount, double durationMs);

    [LoggerMessage(1008, LogLevel.Information, "Execution cancelled after {DurationMs:F0} ms (host shutdown or manual stop); this is not an error")]
    public static partial void ExecutionCancelled(ILogger logger, double durationMs);

    [LoggerMessage(1009, LogLevel.Error, "Execution timed out after {DurationMs:F0} ms (limit {Timeout})")]
    public static partial void ExecutionTimedOut(ILogger logger, double durationMs, TimeSpan? timeout);

    [LoggerMessage(1010, LogLevel.Warning, "Transient failure on attempt {AttemptNumber}; retry {RetryNumber}/{MaxRetries} in {Delay}")]
    public static partial void RetryScheduled(ILogger logger, Exception exception, int attemptNumber, int retryNumber, int maxRetries, TimeSpan delay);

    [LoggerMessage(1011, LogLevel.Information, "Retry attempt {AttemptNumber} starting")]
    public static partial void RetryStarted(ILogger logger, int attemptNumber);

    [LoggerMessage(1012, LogLevel.Information, "Retry succeeded on attempt {AttemptNumber}")]
    public static partial void RetrySucceeded(ILogger logger, int attemptNumber);

    [LoggerMessage(1013, LogLevel.Error, "Retries exhausted after {AttemptCount} attempt(s); execution failed")]
    public static partial void RetriesExhausted(ILogger logger, Exception exception, int attemptCount);

    [LoggerMessage(1014, LogLevel.Debug, "Job lock acquired")]
    public static partial void LockAcquired(ILogger logger);

    [LoggerMessage(1015, LogLevel.Warning, "Job lock unavailable; occurrence skipped")]
    public static partial void LockUnavailable(ILogger logger);

    [LoggerMessage(1016, LogLevel.Warning, "Occurrence {ScheduledAtUtc:o} skipped: previous execution still running (policy={Policy})")]
    public static partial void OverlappingExecutionSkipped(ILogger logger, DateTimeOffset scheduledAtUtc, OverlapPolicy policy);

    [LoggerMessage(1017, LogLevel.Information, "Occurrence {ScheduledAtUtc:o} queued behind the running execution")]
    public static partial void OverlappingExecutionQueued(ILogger logger, DateTimeOffset scheduledAtUtc);

    [LoggerMessage(1018, LogLevel.Debug, "Checkpoint loaded (type={CheckpointType}, updatedAt={UpdatedAtUtc:o})")]
    public static partial void CheckpointLoaded(ILogger logger, string? checkpointType, DateTimeOffset updatedAtUtc);

    [LoggerMessage(1019, LogLevel.Debug, "Checkpoint saved: {CheckpointSummary}")]
    public static partial void CheckpointSaved(ILogger logger, string checkpointSummary);

    [LoggerMessage(1020, LogLevel.Debug, "Idempotent item skipped (key={IdempotencyKey})")]
    public static partial void IdempotentItemSkipped(ILogger logger, string idempotencyKey);

    [LoggerMessage(1021, LogLevel.Warning, "Dead letter created (scope={Scope}, item={ItemId}): {Reason}")]
    public static partial void DeadLetterCreated(ILogger logger, string scope, string? itemId, string reason);

    [LoggerMessage(1022, LogLevel.Information, "Graceful shutdown started; waiting up to {GracePeriod} for {RunningCount} running execution(s)")]
    public static partial void ShutdownStarted(ILogger logger, TimeSpan gracePeriod, int runningCount);

    [LoggerMessage(1023, LogLevel.Information, "Graceful shutdown completed (allFinished={AllFinished})")]
    public static partial void ShutdownCompleted(ILogger logger, bool allFinished);

    [LoggerMessage(1024, LogLevel.Warning, "Startup recovery marked {Count} stale running execution(s) as abandoned")]
    public static partial void AbandonedExecutionsRecovered(ILogger logger, int count);

    [LoggerMessage(1025, LogLevel.Information, "Occurrence {ScheduledExecutionId} already completed; skipping duplicate")]
    public static partial void DuplicateOccurrenceSkipped(ILogger logger, string scheduledExecutionId);

    [LoggerMessage(1026, LogLevel.Information, "Schedule produces no further occurrences; job now waits for manual triggers only")]
    public static partial void ScheduleExhausted(ILogger logger);

    [LoggerMessage(1027, LogLevel.Information, "Manual trigger requested (executionId={ExecutionId})")]
    public static partial void ManualTriggerRequested(ILogger logger, string executionId);

    [LoggerMessage(1028, LogLevel.Error, "Store operation {Operation} failed; the engine continues but durable state may be incomplete")]
    public static partial void StoreOperationFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(1029, LogLevel.Error, "Scheduler loop for job {JobId} crashed unexpectedly; the job will no longer be scheduled until restart. This is a bug in ResilientWorkerKit — please report it")]
    public static partial void SchedulerLoopCrashed(ILogger logger, Exception exception, string jobId);

    [LoggerMessage(1030, LogLevel.Error, "The execution pipeline for job {JobId} faulted instead of recording a result. This is a bug in ResilientWorkerKit — please report it. The scheduler continues")]
    public static partial void RunnerFaulted(ILogger logger, Exception exception, string jobId);
}
