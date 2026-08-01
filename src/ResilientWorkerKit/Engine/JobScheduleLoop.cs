using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ResilientWorkerKit.Scheduling;

namespace ResilientWorkerKit.Engine;

/// <summary>A manual trigger request routed into a job's schedule loop.</summary>
internal sealed record ManualTriggerRequest(string ExecutionId, TaskCompletionSource<string> Accepted);

/// <summary>
/// The per-job scheduler loop: computes occurrences, applies misfire and overlap policies,
/// prevents duplicate occurrence execution, and hands occurrences to the <see cref="JobRunner"/>.
/// One loop failure can never affect another loop or the host (last-resort catch).
/// </summary>
internal sealed class JobScheduleLoop
{
    private const string TriggerSchedule = "schedule";
    private const string TriggerMisfire = "misfire";
    private const string TriggerStartup = "startup";
    private const string TriggerManual = "manual";
    private const string TriggerQueuedOverlap = "queued-overlap";
    private static readonly TimeSpan MaxDelayChunk = TimeSpan.FromDays(20);

    private readonly JobDefinition _def;
    private readonly JobRunner _runner;
    private readonly IJobExecutionStore _executionStore;
    private readonly JobHealthTracker _health;
    private readonly WorkerKitMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<ManualTriggerRequest> _manualTriggers =
        Channel.CreateUnbounded<ManualTriggerRequest>(new UnboundedChannelOptions { SingleReader = true });

    private readonly List<Task<JobRunResult?>> _runs = new();
    private Task<JobRunResult?>? _currentRun;
    private (JobScheduleOccurrence Occurrence, string Trigger)? _queued;
    private DateTimeOffset? _anchorUtc;
    private DateTimeOffset? _lastCompletedUtc;
    private bool _scheduleExhaustedLogged;
    private Task<bool>? _pendingTriggerWait;

    public JobScheduleLoop(
        JobDefinition definition,
        JobRunner runner,
        IJobExecutionStore executionStore,
        JobHealthTracker health,
        WorkerKitMetrics metrics,
        TimeProvider time,
        ILogger logger)
    {
        _def = definition;
        _runner = runner;
        _executionStore = executionStore;
        _health = health;
        _metrics = metrics;
        _time = time;
        _logger = logger;
    }

    public string JobId => _def.JobId;

    /// <summary>Enqueues a manual trigger; the loop decides (overlap policy) and reports back.</summary>
    public void EnqueueManualTrigger(ManualTriggerRequest request)
        => _manualTriggers.Writer.TryWrite(request);

    /// <summary>Tasks of currently running executions (for graceful shutdown draining).</summary>
    public IReadOnlyList<Task> GetRunningTasks()
    {
        lock (_runs)
        {
            return _runs.Where(t => !t.IsCompleted).Cast<Task>().ToList();
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunCoreAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Last resort: a loop bug must never take down the host or other jobs.
            JobLog.SchedulerLoopCrashed(_logger, ex, _def.JobId);
        }
    }

    private async Task RunCoreAsync(CancellationToken stoppingToken)
    {
        JobLog.JobRegistered(
            _logger, _def.JobId, _def.Schedule?.Describe() ?? "none (startup/manual only)",
            _def.RunOnStartup, _def.OverlapPolicy, _def.MisfirePolicy);

        await RecoverStateAsync(stoppingToken).ConfigureAwait(false);

        if (_def.RunOnStartup)
        {
            var now = _time.GetUtcNow();
            var occurrence = new JobScheduleOccurrence(
                now,
                LocalTimeConverter.ToLocal(now, _def.TimeZone),
                "startup:" + now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            StartRun(occurrence, TriggerStartup, presetExecutionId: null, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HarvestFinishedRuns();

            var next = await ComputeNextOccurrenceAsync(stoppingToken).ConfigureAwait(false);
            _health.OnNextOccurrence(_def.JobId, next?.Occurrence.ScheduledAtUtc);
            if (next is { } n && n.Occurrence.ScheduledAtUtc > _time.GetUtcNow())
            {
                JobLog.JobScheduled(_logger, n.Occurrence.ScheduledAtUtc);
            }

            var delayTask = next is { } upcoming
                ? DelayUntilAsync(upcoming.Occurrence.ScheduledAtUtc, stoppingToken)
                : Task.Delay(Timeout.InfiniteTimeSpan, _time, stoppingToken);

            // Keep a single pending channel wait across iterations: the channel is created with
            // SingleReader = true, so there must never be two concurrent WaitToReadAsync calls.
            _pendingTriggerWait ??= _manualTriggers.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var triggerTask = _pendingTriggerWait;

            var waitTasks = new List<Task> { delayTask, triggerTask };
            if (_queued is not null && _currentRun is { IsCompleted: false })
            {
                waitTasks.Add(_currentRun);
            }

            Task finished;
            try
            {
                finished = await Task.WhenAny(waitTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (finished == triggerTask)
            {
                _pendingTriggerWait = null;
                DrainManualTriggers(stoppingToken);
                continue;
            }

            if (finished != delayTask)
            {
                // The running execution finished and something is queued behind it.
                HarvestFinishedRuns();
                if (_queued is { } queued)
                {
                    _queued = null;
                    await FireOccurrenceAsync(queued.Occurrence, TriggerQueuedOverlap, stoppingToken).ConfigureAwait(false);
                }

                continue;
            }

            // Delay elapsed: the occurrence is due.
            if (delayTask.IsFaulted || delayTask.IsCanceled)
            {
                break;
            }

            if (next is { } due)
            {
                await FireOccurrenceAsync(due.Occurrence, due.Trigger, stoppingToken).ConfigureAwait(false);
            }
        }

        // Drain: resolve queued manual triggers so callers do not hang forever.
        while (_manualTriggers.Reader.TryRead(out var pending))
        {
            pending.Accepted.TrySetCanceled(CancellationToken.None);
        }
    }

    private async Task RecoverStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var recent = await _executionStore.GetRecentAsync(_def.JobId, 20, cancellationToken).ConfigureAwait(false);

            // Anchor only on schedule-driven occurrences; startup/manual runs are out-of-band
            // and must not shift the schedule phase across restarts.
            _anchorUtc = recent
                .FirstOrDefault(r => r.TriggerType is TriggerSchedule or TriggerMisfire or TriggerQueuedOverlap)
                ?.ScheduledAtUtc;

            _lastCompletedUtc = recent
                .Where(r => r.CompletedAtUtc is not null)
                .Select(r => r.CompletedAtUtc)
                .FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(_logger, ex, "RecoverScheduleState");
        }
    }

    private async Task<(JobScheduleOccurrence Occurrence, string Trigger)?> ComputeNextOccurrenceAsync(CancellationToken cancellationToken)
    {
        if (_def.Schedule is null)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var context = new ScheduleCalculationContext(now, _lastCompletedUtc, _def.TimeZone);
        var after = _anchorUtc ?? (_def.Schedule is OneTimeSchedule ? DateTimeOffset.MinValue : now);

        var occurrence = _def.Schedule.GetOccurrenceAfter(after, context);
        if (occurrence is null)
        {
            if (!_scheduleExhaustedLogged)
            {
                _scheduleExhaustedLogged = true;
                JobLog.ScheduleExhausted(_logger);
            }

            return null;
        }

        if (occurrence.ScheduledAtUtc > now)
        {
            return (occurrence, TriggerSchedule);
        }

        // One or more occurrences were missed; find the most recent missed one.
        var lastMissed = occurrence;
        for (var i = 0; i < 1_000_000; i++)
        {
            var following = _def.Schedule.GetOccurrenceAfter(lastMissed.ScheduledAtUtc, context);
            if (following is null || following.ScheduledAtUtc > now)
            {
                break;
            }

            lastMissed = following;
        }

        var lateBy = now - lastMissed.ScheduledAtUtc;
        JobLog.MisfireDetected(_logger, lastMissed.ScheduledAtUtc, lateBy, _def.MisfirePolicy);
        _metrics.MisfireDetected(_def.JobId, _def.MisfirePolicy);

        switch (_def.MisfirePolicy)
        {
            case MisfirePolicy.RunImmediatelyOnce:
                return await RecoverMissedOccurrenceAsync(lastMissed, cancellationToken).ConfigureAwait(false);

            case MisfirePolicy.RunIfWithinTolerance when _def.MisfireTolerance is { } tolerance && lateBy <= tolerance:
                return await RecoverMissedOccurrenceAsync(lastMissed, cancellationToken).ConfigureAwait(false);

            case MisfirePolicy.RescheduleFromNow:
                _anchorUtc = now;
                var reanchored = _def.Schedule.GetOccurrenceAfter(now, context);
                return reanchored is null ? null : (reanchored, TriggerSchedule);

            case MisfirePolicy.Skip:
            case MisfirePolicy.RunIfWithinTolerance:
            default:
                _anchorUtc = lastMissed.ScheduledAtUtc;
                JobLog.MisfireSkipped(_logger, lastMissed.ScheduledAtUtc);
                var upcoming = _def.Schedule.GetOccurrenceAfter(lastMissed.ScheduledAtUtc, context);
                return upcoming is null ? null : (upcoming, TriggerSchedule);
        }
    }

    private async Task<(JobScheduleOccurrence Occurrence, string Trigger)?> RecoverMissedOccurrenceAsync(
        JobScheduleOccurrence missed, CancellationToken cancellationToken)
    {
        // Restart safety: never create the same missed occurrence twice. Any execution record
        // for its identity (regardless of status) means it was already attempted.
        var scheduledExecutionId = $"{_def.JobId}:{missed.IdentityToken}";
        bool alreadyAttempted;
        try
        {
            alreadyAttempted = await _executionStore
                .ExistsForScheduledExecutionAsync(_def.JobId, scheduledExecutionId, completedOnly: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(_logger, ex, "CheckMissedOccurrence");
            alreadyAttempted = true; // fail safe: prefer skipping over double-running
        }

        if (alreadyAttempted)
        {
            _anchorUtc = missed.ScheduledAtUtc;
            JobLog.MisfireSkipped(_logger, missed.ScheduledAtUtc);
            var context = new ScheduleCalculationContext(_time.GetUtcNow(), _lastCompletedUtc, _def.TimeZone);
            var upcoming = _def.Schedule!.GetOccurrenceAfter(missed.ScheduledAtUtc, context);
            return upcoming is null ? null : (upcoming, TriggerSchedule);
        }

        return (missed, TriggerMisfire);
    }

    private async Task FireOccurrenceAsync(JobScheduleOccurrence occurrence, string trigger, CancellationToken stoppingToken)
    {
        _anchorUtc = occurrence.ScheduledAtUtc;
        _scheduleExhaustedLogged = false;

        // Identity dedup: a completed occurrence never runs again (monthly identity, one-time
        // schedules, DST fall-back double-fire protection).
        bool alreadyCompleted;
        try
        {
            alreadyCompleted = await _executionStore
                .ExistsForScheduledExecutionAsync(
                    _def.JobId, $"{_def.JobId}:{occurrence.IdentityToken}", completedOnly: true, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(_logger, ex, "CheckDuplicateOccurrence");
            alreadyCompleted = false;
        }

        if (alreadyCompleted)
        {
            JobLog.DuplicateOccurrenceSkipped(_logger, $"{_def.JobId}:{occurrence.IdentityToken}");
            return;
        }

        HarvestFinishedRuns();
        var busy = _def.OverlapPolicy != OverlapPolicy.AllowConcurrentExecutions
            && _currentRun is { IsCompleted: false };

        if (busy)
        {
            if (_def.OverlapPolicy == OverlapPolicy.QueueSingleExecution && _queued is null)
            {
                _queued = (occurrence, trigger);
                JobLog.OverlappingExecutionQueued(_logger, occurrence.ScheduledAtUtc);
            }
            else
            {
                JobLog.OverlappingExecutionSkipped(_logger, occurrence.ScheduledAtUtc, _def.OverlapPolicy);
            }

            _metrics.OverlapSkipped(_def.JobId);
            return;
        }

        StartRun(occurrence, trigger, presetExecutionId: null, stoppingToken);
    }

    private void StartRun(JobScheduleOccurrence occurrence, string trigger, string? presetExecutionId, CancellationToken stoppingToken)
    {
        var runTask = _runner.RunAsync(_def, occurrence, trigger, presetExecutionId, _logger, stoppingToken);
        lock (_runs)
        {
            _runs.Add(runTask);
        }

        if (_def.OverlapPolicy != OverlapPolicy.AllowConcurrentExecutions)
        {
            _currentRun = runTask;
        }
    }

    private void HarvestFinishedRuns()
    {
        lock (_runs)
        {
            for (var i = _runs.Count - 1; i >= 0; i--)
            {
                var task = _runs[i];
                if (!task.IsCompleted)
                {
                    continue;
                }

                if (task.IsCompletedSuccessfully && task.Result is { } result)
                {
                    if (_lastCompletedUtc is not { } last || result.CompletedAtUtc > last)
                    {
                        _lastCompletedUtc = result.CompletedAtUtc;
                    }
                }

                _runs.RemoveAt(i);
            }
        }
    }

    private void DrainManualTriggers(CancellationToken stoppingToken)
    {
        while (_manualTriggers.Reader.TryRead(out var request))
        {
            JobLog.ManualTriggerRequested(_logger, request.ExecutionId);
            HarvestFinishedRuns();

            var busy = _def.OverlapPolicy != OverlapPolicy.AllowConcurrentExecutions
                && _currentRun is { IsCompleted: false };
            if (busy)
            {
                request.Accepted.TrySetException(new InvalidOperationException(
                    $"Job '{_def.JobId}' is currently running and its overlap policy ({_def.OverlapPolicy}) rejected the manual trigger."));
                continue;
            }

            var now = _time.GetUtcNow();
            var occurrence = new JobScheduleOccurrence(
                now,
                LocalTimeConverter.ToLocal(now, _def.TimeZone),
                "manual:" + request.ExecutionId);

            StartRun(occurrence, TriggerManual, request.ExecutionId, stoppingToken);
            request.Accepted.TrySetResult(request.ExecutionId);
        }
    }

    private async Task DelayUntilAsync(DateTimeOffset targetUtc, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = targetUtc - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var chunk = remaining > MaxDelayChunk ? MaxDelayChunk : remaining;
            await Task.Delay(chunk, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
