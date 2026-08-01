using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit.Engine;

/// <summary>Result of one execution run (all attempts).</summary>
internal sealed record JobRunResult(string ExecutionId, JobExecutionStatus Status, DateTimeOffset CompletedAtUtc);

/// <summary>
/// Executes one job occurrence: DI scope per attempt, retry with failure classification,
/// attempt/total timeouts, execution history, health, metrics and dead-lettering.
/// Never throws — the execution boundary is absolute so a job failure can never reach the
/// scheduler loop or the host.
/// </summary>
internal sealed class JobRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobExecutionStore _executionStore;
    private readonly IJobCheckpointStore _checkpointStore;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly IJobLockProvider _lockProvider;
    private readonly IJobFailureClassifier _classifier;
    private readonly JobHealthTracker _health;
    private readonly WorkerKitMetrics _metrics;
    private readonly WorkerKitOptions _options;
    private readonly TimeProvider _time;

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        IJobExecutionStore executionStore,
        IJobCheckpointStore checkpointStore,
        IIdempotencyStore idempotencyStore,
        IDeadLetterStore deadLetterStore,
        IJobLockProvider lockProvider,
        IJobFailureClassifier classifier,
        JobHealthTracker health,
        WorkerKitMetrics metrics,
        WorkerKitOptions options,
        TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _executionStore = executionStore;
        _checkpointStore = checkpointStore;
        _idempotencyStore = idempotencyStore;
        _deadLetterStore = deadLetterStore;
        _lockProvider = lockProvider;
        _classifier = classifier;
        _health = health;
        _metrics = metrics;
        _options = options;
        _time = time;
    }

    /// <summary>
    /// Runs one occurrence. Returns null when the occurrence was skipped because the job lock
    /// was unavailable; otherwise returns the final status.
    /// </summary>
    public async Task<JobRunResult?> RunAsync(
        JobDefinition definition,
        JobScheduleOccurrence occurrence,
        string triggerType,
        string? presetExecutionId,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        IAsyncDisposable? lockHandle = null;
        if (definition.OverlapPolicy != OverlapPolicy.AllowConcurrentExecutions)
        {
            try
            {
                lockHandle = await _lockProvider
                    .TryAcquireAsync(definition.JobId, _options.LockAcquireTimeout, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (lockHandle is null)
            {
                JobLog.LockUnavailable(logger);
                return null;
            }

            JobLog.LockAcquired(logger);
        }

        try
        {
            return await RunCoreAsync(definition, occurrence, triggerType, presetExecutionId, logger, stoppingToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (lockHandle is not null)
            {
                await lockHandle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<JobRunResult> RunCoreAsync(
        JobDefinition definition,
        JobScheduleOccurrence occurrence,
        string triggerType,
        string? presetExecutionId,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        var executionId = presetExecutionId ?? Guid.NewGuid().ToString("n");
        var scheduledExecutionId = $"{definition.JobId}:{occurrence.IdentityToken}";
        var startedAt = _time.GetUtcNow();

        var record = new JobExecutionRecord
        {
            JobId = definition.JobId,
            ExecutionId = executionId,
            ScheduledExecutionId = scheduledExecutionId,
            ScheduledAtUtc = occurrence.ScheduledAtUtc,
            ScheduledLocalTime = occurrence.ScheduledLocalTime,
            TimeZoneId = definition.TimeZone.Id,
            TriggerType = triggerType,
            StartedAtUtc = startedAt,
            CorrelationId = executionId,
            HostInstanceId = _options.HostInstanceId,
            CreatedAtUtc = startedAt,
            UpdatedAtUtc = startedAt,
        };

        using var executionScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobId"] = definition.JobId,
            ["ExecutionId"] = executionId,
            ["ScheduledExecutionId"] = scheduledExecutionId,
            ["CorrelationId"] = record.CorrelationId,
            ["HostInstanceId"] = record.HostInstanceId,
        });

        using var activity = WorkerKitMetrics.ActivitySource.StartActivity("workerkit.job.execute");
        activity?.SetTag("workerkit.job.id", definition.JobId);
        activity?.SetTag("workerkit.execution.id", executionId);
        activity?.SetTag("workerkit.trigger", triggerType);

        await SafeStoreAsync(() => _executionStore.CreateAsync(record, CancellationToken.None), "CreateExecution", logger)
            .ConfigureAwait(false);

        _health.OnExecutionStarted(definition.JobId, startedAt, occurrence.ScheduledAtUtc);
        _metrics.ExecutionStarted(definition.JobId);
        JobLog.ExecutionStarting(logger, triggerType, occurrence.ScheduledAtUtc);

        using var timeoutCts = definition.Timeout is { } totalTimeout
            ? new CancellationTokenSource(totalTimeout, _time)
            : new CancellationTokenSource();
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

        var items = new Dictionary<string, object?>();
        var attempt = 0;
        JobExecutionStatus finalStatus;
        JobFailureKind? finalKind = null;
        Exception? finalException = null;
        var retriesExhausted = false;

        while (true)
        {
            attempt++;
            record.AttemptCount = attempt;
            using var attemptScope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["AttemptNumber"] = attempt,
            });

            CancellationTokenSource? attemptCts = null;
            CancellationTokenSource? attemptLinked = null;
            try
            {
                if (definition.Retry.AttemptTimeout is { } attemptTimeout)
                {
                    attemptCts = new CancellationTokenSource(attemptTimeout, _time);
                    attemptLinked = CancellationTokenSource.CreateLinkedTokenSource(executionCts.Token, attemptCts.Token);
                }

                var token = attemptLinked?.Token ?? executionCts.Token;
                JobLog.AttemptStarted(logger, attempt);

                var scope = _scopeFactory.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var context = CreateContext(
                        definition, occurrence, record, executionId, scheduledExecutionId,
                        attempt, startedAt, scope.ServiceProvider, logger, items, token);

                    var job = (IWorkerJob)scope.ServiceProvider.GetRequiredService(definition.JobType);
                    await job.ExecuteAsync(context, token).ConfigureAwait(false);
                }

                if (attempt > 1)
                {
                    JobLog.RetrySucceeded(logger, attempt);
                }

                finalStatus = JobExecutionStatus.Completed;
                break;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
                {
                    (finalStatus, finalKind, finalException) = (JobExecutionStatus.Cancelled, JobFailureKind.Cancelled, ex);
                    break;
                }

                if (ex is OperationCanceledException && timeoutCts.IsCancellationRequested)
                {
                    (finalStatus, finalKind, finalException) = (JobExecutionStatus.TimedOut, JobFailureKind.TimedOut, ex);
                    break;
                }

                var classification = ex is OperationCanceledException && attemptCts?.IsCancellationRequested == true
                    ? JobFailureClassification.Transient // attempt timeout: retry-eligible
                    : SafeClassify(ex, logger);

                if (classification.Kind == JobFailureKind.Transient && attempt <= definition.Retry.MaxRetries)
                {
                    var delay = RetryDelayCalculator.Compute(
                        definition.Retry, attempt, classification.RetryAfter, Random.Shared.NextDouble());
                    JobLog.RetryScheduled(logger, ex, attempt, attempt, definition.Retry.MaxRetries, delay);
                    _metrics.RetryScheduled(definition.JobId);
                    record.UpdatedAtUtc = _time.GetUtcNow();
                    await SafeStoreAsync(() => _executionStore.UpdateAsync(record, CancellationToken.None), "UpdateExecution", logger)
                        .ConfigureAwait(false);

                    try
                    {
                        await Task.Delay(delay, _time, executionCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce)
                    {
                        (finalStatus, finalKind, finalException) = stoppingToken.IsCancellationRequested
                            ? (JobExecutionStatus.Cancelled, JobFailureKind.Cancelled, (Exception)oce)
                            : (JobExecutionStatus.TimedOut, JobFailureKind.TimedOut, oce);
                        break;
                    }

                    JobLog.RetryStarted(logger, attempt + 1);
                    continue;
                }

                finalException = ex;
                finalKind = classification.Kind;
                retriesExhausted = classification.Kind == JobFailureKind.Transient;
                finalStatus = classification.Kind switch
                {
                    JobFailureKind.Cancelled => JobExecutionStatus.Cancelled,
                    JobFailureKind.TimedOut => JobExecutionStatus.TimedOut,
                    _ => JobExecutionStatus.Failed,
                };
                break;
            }
            finally
            {
                attemptLinked?.Dispose();
                attemptCts?.Dispose();
            }
        }

        var completedAt = _time.GetUtcNow();
        var durationMs = (completedAt - startedAt).TotalMilliseconds;

        record.CompletedAtUtc = completedAt;
        record.Status = finalStatus;
        record.FailureKind = finalKind;
        record.DurationMs = durationMs;
        record.UpdatedAtUtc = completedAt;
        if (finalException is not null)
        {
            // Masked before persisting: an exception message can carry a token or connection
            // string, and the execution history is long-lived and widely readable.
            record.ErrorType = finalException.GetType().FullName;
            record.ErrorMessage = Truncate(SensitiveDataMasker.MaskSecrets(finalException.Message), 500);
            record.ErrorDetail = Truncate(SensitiveDataMasker.MaskSecrets(finalException.ToString()), 4000);
        }

        // The dead letter is written *before* the execution record reaches its terminal status,
        // so that observing a Failed execution guarantees its dead letter already exists. The
        // reverse order leaves a window where a reader sees the failure but not the record it
        // needs to act on.
        if (finalStatus == JobExecutionStatus.Failed && definition.DeadLetterOnFailure)
        {
            var deadLetter = new DeadLetterRecord
            {
                Id = Guid.NewGuid().ToString("n"),
                JobId = definition.JobId,
                ExecutionId = executionId,
                Scope = "execution",
                FailureKind = finalKind,
                Reason = record.ErrorMessage ?? finalKind?.ToString() ?? "unknown failure",
                AttemptCount = attempt,
                CreatedAtUtc = completedAt,
            };
            await SafeStoreAsync(() => _deadLetterStore.AddAsync(deadLetter, CancellationToken.None), "AddDeadLetter", logger)
                .ConfigureAwait(false);
            JobLog.DeadLetterCreated(logger, "execution", null, deadLetter.Reason);
            _metrics.DeadLetterCreated(definition.JobId);
        }

        await SafeStoreAsync(() => _executionStore.UpdateAsync(record, CancellationToken.None), "UpdateExecution", logger)
            .ConfigureAwait(false);

        switch (finalStatus)
        {
            case JobExecutionStatus.Completed:
                JobLog.ExecutionCompleted(logger, durationMs, attempt);
                break;
            case JobExecutionStatus.Cancelled:
                JobLog.ExecutionCancelled(logger, durationMs);
                break;
            case JobExecutionStatus.TimedOut:
                JobLog.ExecutionTimedOut(logger, durationMs, definition.Timeout);
                break;
            case JobExecutionStatus.Failed when retriesExhausted:
                JobLog.RetriesExhausted(logger, finalException!, attempt);
                break;
            default:
                JobLog.ExecutionFailed(logger, finalException!, finalKind ?? JobFailureKind.Permanent, attempt, durationMs);
                break;
        }

        _health.OnExecutionFinished(definition.JobId, finalStatus, completedAt, durationMs);
        _metrics.ExecutionFinished(definition.JobId, finalStatus, durationMs / 1000d);

        activity?.SetTag("workerkit.attempts", attempt);
        activity?.SetTag("workerkit.status", finalStatus.ToString());
        if (finalStatus is JobExecutionStatus.Failed or JobExecutionStatus.TimedOut)
        {
            activity?.SetStatus(ActivityStatusCode.Error, record.ErrorMessage);
        }

        return new JobRunResult(executionId, finalStatus, completedAt);
    }

    private JobExecutionContext CreateContext(
        JobDefinition definition,
        JobScheduleOccurrence occurrence,
        JobExecutionRecord record,
        string executionId,
        string scheduledExecutionId,
        int attempt,
        DateTimeOffset startedAt,
        IServiceProvider scopedServices,
        ILogger logger,
        IDictionary<string, object?> items,
        CancellationToken token)
    {
        var checkpoints = new JobCheckpointAccessor(
            _checkpointStore, definition.JobId, _options.JsonSerializerOptions, _time, logger,
            onSaved: summary =>
            {
                record.LastCheckpointSummary = summary;
                _health.OnCheckpointSaved(definition.JobId, summary);
            });

        var idempotency = new JobIdempotencyAccessor(
            _idempotencyStore, definition.JobId, executionId, definition.IdempotencyTimeToLive, _time, logger);

        var deadLetters = new JobDeadLetterAccessor(
            _deadLetterStore, definition.JobId, executionId, _time, logger, _metrics);

        return new JobExecutionContext
        {
            JobId = definition.JobId,
            DisplayName = definition.DisplayName,
            ExecutionId = executionId,
            ScheduledExecutionId = scheduledExecutionId,
            AttemptNumber = attempt,
            ScheduledAtUtc = occurrence.ScheduledAtUtc,
            ScheduledLocalTime = occurrence.ScheduledLocalTime,
            TimeZoneId = definition.TimeZone.Id,
            StartedAtUtc = startedAt,
            CorrelationId = executionId,
            HostInstanceId = _options.HostInstanceId,
            Services = scopedServices,
            Logger = logger,
            Checkpoints = checkpoints,
            Idempotency = idempotency,
            DeadLetters = deadLetters,
            Items = items,
            CancellationToken = token,
            ProgressReporter = _health,
        };
    }

    private JobFailureClassification SafeClassify(Exception exception, ILogger logger)
    {
        try
        {
            return _classifier.Classify(exception);
        }
        catch (Exception classifierError)
        {
            JobLog.StoreOperationFailed(logger, classifierError, "ClassifyFailure");
            return JobFailureClassification.Transient;
        }
    }

    private static async Task SafeStoreAsync(Func<Task> operation, string operationName, ILogger logger)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(logger, ex, operationName);
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
