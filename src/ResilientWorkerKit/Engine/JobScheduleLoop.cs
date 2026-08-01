using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ResilientWorkerKit.Scheduling;

namespace ResilientWorkerKit.Engine;

/// <summary>A manual trigger request routed into a job's schedule loop.</summary>
internal sealed record ManualTriggerRequest(string ExecutionId, TaskCompletionSource<string> Accepted);

/// <summary>
/// The next thing the loop intends to run: either an occurrence derived from the job's schedule,
/// or one taken from the durable pending queue (a follow-up retry).
/// </summary>
/// <param name="Occurrence">When it is due, and the identity it will execute under.</param>
/// <param name="Trigger">Trigger type recorded on the execution.</param>
/// <param name="Pending">The durable row backing this work, when it came from the queue.</param>
/// <param name="IsOutOfBand">
/// True for work that did not come from the schedule. Out-of-band work must never move the
/// schedule anchor — a retry deciding when the next scheduled occurrence is due would let an
/// unrelated failure skip a month of a monthly job. This is carried explicitly rather than
/// inferred from the pending row, which does not survive being queued behind a running execution.
/// </param>
internal sealed record NextWork(
    JobScheduleOccurrence Occurrence,
    string Trigger,
    PendingOccurrence? Pending = null,
    bool IsOutOfBand = false);

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
    private const string TriggerFollowUp = "follow-up";
    private static readonly TimeSpan MaxDelayChunk = TimeSpan.FromDays(20);

    private readonly JobDefinition _def;
    private readonly JobRunner _runner;
    private readonly IJobExecutionStore _executionStore;
    private readonly IPendingOccurrenceStore _pendingStore;
    private readonly JobHealthTracker _health;
    private readonly WorkerKitMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<ManualTriggerRequest> _manualTriggers =
        Channel.CreateUnbounded<ManualTriggerRequest>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Raised whenever durable work is queued for this job. The loop computes what to do once
    /// per iteration and then sleeps — potentially forever, when the schedule has no further
    /// occurrences — so anything that queues work after that decision has to say so, or the work
    /// sits in the store with nobody watching it.
    /// </summary>
    private readonly Channel<byte> _wakeSignal =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly List<Task<JobRunResult?>> _runs = new();
    private Task<JobRunResult?>? _currentRun;
    private NextWork? _queued;
    private DateTimeOffset? _anchorUtc;
    private DateTimeOffset? _lastCompletedUtc;
    private bool _scheduleExhaustedLogged;
    private Task<bool>? _pendingTriggerWait;
    private Task<bool>? _pendingWakeWait;

    public JobScheduleLoop(
        JobDefinition definition,
        JobRunner runner,
        IJobExecutionStore executionStore,
        IPendingOccurrenceStore pendingStore,
        JobHealthTracker health,
        WorkerKitMetrics metrics,
        TimeProvider time,
        ILogger logger)
    {
        _def = definition;
        _runner = runner;
        _executionStore = executionStore;
        _pendingStore = pendingStore;
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
            StartRun(occurrence, TriggerStartup, presetExecutionId: null, followUpOrdinal: 0,
                originScheduledExecutionId: null, claimed: null, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HarvestFinishedRuns();

            var next = await ComputeNextWorkAsync(stoppingToken).ConfigureAwait(false);
            _health.OnNextOccurrence(_def.JobId, next?.Occurrence.ScheduledAtUtc);
            if (next is { } n && n.Occurrence.ScheduledAtUtc > _time.GetUtcNow())
            {
                JobLog.JobScheduled(_logger, n.Occurrence.ScheduledAtUtc);
            }

            var inFlight = SnapshotInFlightRuns();
            var atCapacity = _def.OverlapPolicy != OverlapPolicy.AllowConcurrentExecutions && inFlight.Count > 0;

            // The wait is scoped to this iteration. Anything that ends the iteration early — a
            // manual trigger, queued work, a finished execution — cancels and observes the delay,
            // so pending timers cannot accumulate and their cancellation is never left unobserved.
            using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask = BuildDelay(next, atCapacity, iterationCts.Token);

            // Both channel waits are kept across iterations: the channels are SingleReader, so
            // there must never be two concurrent WaitToReadAsync calls on one of them. They are
            // therefore bound to stoppingToken rather than to the iteration.
            _pendingTriggerWait ??= _manualTriggers.Reader.WaitToReadAsync(stoppingToken).AsTask();
            _pendingWakeWait ??= _wakeSignal.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var triggerTask = _pendingTriggerWait;
            var wakeTask = _pendingWakeWait;

            var waitTasks = new List<Task> { delayTask, triggerTask, wakeTask };

            // Always wait on executions that are still running. A follow-up is queued from inside
            // the run's own task, so the completion of that task is the signal that new durable
            // work may exist. Waiting on it only when something was already queued was why a
            // follow-up from a job that awaits was never picked up by its own host.
            waitTasks.AddRange(inFlight);

            var finished = await Task.WhenAny(waitTasks).ConfigureAwait(false);

            if (finished != delayTask)
            {
                await CancelAndObserveAsync(iterationCts, delayTask).ConfigureAwait(false);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (finished == wakeTask)
            {
                _pendingWakeWait = null;
                if (!wakeTask.IsCompletedSuccessfully)
                {
                    break;
                }

                _wakeSignal.Reader.TryRead(out _);
                continue;   // recompute: durable work was queued
            }

            if (finished == triggerTask)
            {
                if (triggerTask.IsCanceled || triggerTask.IsFaulted)
                {
                    break;
                }

                _pendingTriggerWait = null;
                DrainManualTriggers(stoppingToken);
                continue;
            }

            if (finished != delayTask)
            {
                // An execution finished: capacity may be free and durable work may have been
                // queued by that execution. Run anything held behind it, then recompute.
                HarvestFinishedRuns();
                if (_queued is { } queued)
                {
                    _queued = null;
                    var trigger = queued.IsOutOfBand ? queued.Trigger : TriggerQueuedOverlap;
                    await FireOccurrenceAsync(queued with { Trigger = trigger }, stoppingToken).ConfigureAwait(false);
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
                await FireOccurrenceAsync(due, stoppingToken).ConfigureAwait(false);
            }
        }

        await ObserveAsync(_pendingTriggerWait).ConfigureAwait(false);
        _pendingTriggerWait = null;

        // Drain: resolve queued manual triggers so callers do not hang forever.
        while (_manualTriggers.Reader.TryRead(out var pending))
        {
            pending.Accepted.TrySetCanceled(CancellationToken.None);
        }
    }

    /// <summary>Snapshot of executions that have not finished yet.</summary>
    private List<Task> SnapshotInFlightRuns()
    {
        lock (_runs)
        {
            return _runs.Where(t => !t.IsCompleted).Cast<Task>().ToList();
        }
    }

    /// <summary>
    /// How long to sleep this iteration.
    /// <para>
    /// Out-of-band work that is already due but cannot start because the job is busy waits for
    /// capacity rather than for a clock. Sleeping until its due time would be a no-op — the time
    /// has passed — so the loop would spin; and dropping it is not an option, because a durably
    /// planned action must not be lost just because the job happened to be running.
    /// </para>
    /// </summary>
    private Task BuildDelay(NextWork? next, bool atCapacity, CancellationToken token)
    {
        if (next is not { } work)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan, _time, token);
        }

        if (atCapacity && work.IsOutOfBand && work.Occurrence.ScheduledAtUtc <= _time.GetUtcNow())
        {
            return Task.Delay(Timeout.InfiniteTimeSpan, _time, token);
        }

        return DelayUntilAsync(work.Occurrence.ScheduledAtUtc, token);
    }

    /// <summary>
    /// Tells the loop that durable work exists for this job. Safe to call from any thread and
    /// from inside a running execution; a signal already pending is simply coalesced.
    /// </summary>
    private void SignalWake() => _wakeSignal.Writer.TryWrite(0);

    /// <summary>Cancels the iteration's wait and observes the resulting task so it is never left faulted-and-unread.</summary>
    private static async Task CancelAndObserveAsync(CancellationTokenSource cts, Task delayTask)
    {
        await cts.CancelAsync().ConfigureAwait(false);
        await ObserveAsync(delayTask).ConfigureAwait(false);
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the wait was cancelled because the iteration ended for another reason.
        }
        catch (Exception)
        {
            // A delay/channel wait cannot fail meaningfully; observing is enough to keep the
            // exception from surfacing as an unobserved task exception later.
        }
    }

    private async Task RecoverStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The window is generous because only schedule-driven records can anchor the phase:
            // a job with a long run of manual/startup executions would otherwise lose its anchor
            // and silently re-anchor to "now".
            var recent = await _executionStore.GetRecentAsync(_def.JobId, 200, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// The next work item: the earlier of the schedule's next occurrence and the durable pending
    /// queue's head. Follow-up retries therefore compete with the schedule on equal terms, and a
    /// follow-up queued before a restart is picked up by the new process.
    /// </summary>
    private async Task<NextWork?> ComputeNextWorkAsync(CancellationToken cancellationToken)
    {
        var scheduled = await ComputeNextOccurrenceAsync(cancellationToken).ConfigureAwait(false);
        var pending = await GetNextPendingAsync(cancellationToken).ConfigureAwait(false);

        if (pending is null)
        {
            return scheduled;
        }

        if (scheduled is not null && scheduled.Occurrence.ScheduledAtUtc <= pending.DueAtUtc)
        {
            return scheduled;
        }

        return new NextWork(
            new JobScheduleOccurrence(
                pending.DueAtUtc,
                LocalTimeConverter.ToLocal(pending.DueAtUtc, _def.TimeZone),
                pending.IdentityToken),
            TriggerFollowUp,
            pending,
            IsOutOfBand: true);
    }

    private async Task<PendingOccurrence?> GetNextPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _pendingStore.GetNextAsync(_def.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(_logger, ex, "GetNextPendingOccurrence");
            return null;
        }
    }

    private async Task<NextWork?> ComputeNextOccurrenceAsync(CancellationToken cancellationToken)
    {
        if (_def.Schedule is null)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var context = new ScheduleCalculationContext(now, _lastCompletedUtc, _def.TimeZone);
        var after = _anchorUtc
            ?? (_def.Schedule.DiscoverPastOccurrencesOnFirstStart ? DateTimeOffset.MinValue : now);

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
            return new NextWork(occurrence, TriggerSchedule);
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
                return reanchored is null ? null : new NextWork(reanchored, TriggerSchedule);

            case MisfirePolicy.Skip:
            case MisfirePolicy.RunIfWithinTolerance:
            default:
                _anchorUtc = lastMissed.ScheduledAtUtc;
                JobLog.MisfireSkipped(_logger, lastMissed.ScheduledAtUtc);
                var upcoming = _def.Schedule.GetOccurrenceAfter(lastMissed.ScheduledAtUtc, context);
                return upcoming is null ? null : new NextWork(upcoming, TriggerSchedule);
        }
    }

    private async Task<NextWork?> RecoverMissedOccurrenceAsync(
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
            return upcoming is null ? null : new NextWork(upcoming, TriggerSchedule);
        }

        return new NextWork(missed, TriggerMisfire);
    }

    private async Task FireOccurrenceAsync(NextWork work, CancellationToken stoppingToken)
    {
        var occurrence = work.Occurrence;
        var trigger = work.Trigger;

        // Out-of-band work never advances the schedule phase: a retry must not decide when the
        // next scheduled occurrence is due.
        if (!work.IsOutOfBand)
        {
            _anchorUtc = occurrence.ScheduledAtUtc;
        }

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
            // Durable out-of-band work is never dropped for being late to the lock. Its row stays
            // in the queue, unclaimed, and the loop retries it once capacity frees up — the
            // overlap policy governs schedule occurrences, not planned actions that must happen.
            if (work.IsOutOfBand)
            {
                JobLog.OutOfBandWorkDeferred(_logger, occurrence.ScheduledAtUtc, trigger);
                return;
            }

            if (_def.OverlapPolicy == OverlapPolicy.QueueSingleExecution && _queued is null)
            {
                _queued = work;
                JobLog.OverlappingExecutionQueued(_logger, occurrence.ScheduledAtUtc);
            }
            else
            {
                // Only a genuinely dropped occurrence counts as skipped; a queued one still runs.
                JobLog.OverlappingExecutionSkipped(_logger, occurrence.ScheduledAtUtc, _def.OverlapPolicy);
                _metrics.OverlapSkipped(_def.JobId);
            }

            return;
        }

        // Claim last: the claim is a delete, so claiming before the checks above would destroy a
        // durable occurrence that then turns out not to run.
        if (work.Pending is { } pending)
        {
            bool claimed;
            try
            {
                claimed = await _pendingStore.TryClaimAsync(pending.Id, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                JobLog.StoreOperationFailed(_logger, ex, "ClaimPendingOccurrence");
                return;
            }

            if (!claimed)
            {
                return;   // another runner won it
            }

            JobLog.FollowUpStarting(_logger, pending.FollowUpOrdinal, pending.OriginScheduledExecutionId);
        }

        StartRun(occurrence, trigger, presetExecutionId: null, work.Pending?.FollowUpOrdinal ?? 0,
            work.Pending?.OriginScheduledExecutionId, work.Pending, stoppingToken);
    }

    private void StartRun(
        JobScheduleOccurrence occurrence,
        string trigger,
        string? presetExecutionId,
        int followUpOrdinal,
        string? originScheduledExecutionId,
        PendingOccurrence? claimed,
        CancellationToken stoppingToken)
    {
        var runTask = RunAndPlanFollowUpAsync(
            occurrence, trigger, presetExecutionId, followUpOrdinal, originScheduledExecutionId,
            claimed, stoppingToken);
        lock (_runs)
        {
            _runs.Add(runTask);
        }

        if (_def.OverlapPolicy != OverlapPolicy.AllowConcurrentExecutions)
        {
            _currentRun = runTask;
        }
    }

    /// <summary>
    /// Runs the occurrence and, when it fails and the job has a follow-up retry policy, queues
    /// the next attempt durably. Queuing happens here rather than in the runner because it is a
    /// scheduling decision, and because only the loop knows which follow-up this run is.
    /// </summary>
    private async Task<JobRunResult?> RunAndPlanFollowUpAsync(
        JobScheduleOccurrence occurrence,
        string trigger,
        string? presetExecutionId,
        int followUpOrdinal,
        string? originScheduledExecutionId,
        PendingOccurrence? claimed,
        CancellationToken stoppingToken)
    {
        var result = await _runner
            .RunAsync(_def, occurrence, trigger, presetExecutionId, _logger, stoppingToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            // The runner declined — the job lock was unavailable. The row is already claimed, so
            // put it back rather than losing a planned action to a lock we did not get.
            await ReturnToQueueAsync(claimed).ConfigureAwait(false);
            return null;
        }

        await PlanFollowUpIfNeededAsync(result, occurrence, followUpOrdinal, originScheduledExecutionId)
            .ConfigureAwait(false);

        return result;
    }

    private async Task ReturnToQueueAsync(PendingOccurrence? claimed)
    {
        if (claimed is null)
        {
            return;
        }

        try
        {
            await _pendingStore.AddAsync(
                new PendingOccurrence
                {
                    Id = Guid.NewGuid().ToString("n"),
                    JobId = claimed.JobId,
                    DueAtUtc = claimed.DueAtUtc,
                    IdentityToken = claimed.IdentityToken,
                    Source = claimed.Source,
                    OriginScheduledExecutionId = claimed.OriginScheduledExecutionId,
                    FollowUpOrdinal = claimed.FollowUpOrdinal,
                    PayloadJson = claimed.PayloadJson,
                    CreatedAtUtc = claimed.CreatedAtUtc,
                },
                CancellationToken.None).ConfigureAwait(false);
            JobLog.OutOfBandWorkReturnedToQueue(_logger, claimed.IdentityToken);
            SignalWake();
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(_logger, ex, "ReturnPendingOccurrence");
        }
    }

    private async Task PlanFollowUpIfNeededAsync(
        JobRunResult result,
        JobScheduleOccurrence occurrence,
        int followUpOrdinal,
        string? originScheduledExecutionId)
    {
        if (_def.FollowUpRetry is not { } policy || result.Status != JobExecutionStatus.Failed)
        {
            return;
        }

        // A deterministic failure normally repeats; retrying it just burns the window unless the
        // job opts in.
        if (!policy.RetryPermanentFailures
            && result.FailureKind is JobFailureKind.Permanent or JobFailureKind.Misconfigured)
        {
            JobLog.FollowUpSkippedForPermanentFailure(_logger, result.FailureKind.Value);
            return;
        }

        var nextOrdinal = followUpOrdinal + 1;
        var origin = originScheduledExecutionId ?? $"{_def.JobId}:{occurrence.IdentityToken}";

        // Derived from the ORIGIN, never from the previous follow-up. Chaining each token onto
        // the last one grew it by a segment per attempt (…+followup-1+followup-2+followup-3),
        // which is both unreadable and able to overflow the 300-character persisted column on a
        // provider that enforces lengths. From the origin, the token is bounded by construction.
        var originIdentity = origin.StartsWith(_def.JobId + ":", StringComparison.Ordinal)
            ? origin[(_def.JobId.Length + 1)..]
            : occurrence.IdentityToken;

        if (nextOrdinal > policy.MaxAttempts)
        {
            JobLog.FollowUpRetriesExhausted(_logger, policy.MaxAttempts, origin);
            return;
        }

        var delay = policy.DelayFor(nextOrdinal);
        var dueAt = _time.GetUtcNow() + delay;

        var pending = new PendingOccurrence
        {
            Id = Guid.NewGuid().ToString("n"),
            JobId = _def.JobId,
            DueAtUtc = dueAt,
            IdentityToken = $"{originIdentity}+followup-{nextOrdinal}",
            Source = PendingOccurrenceSources.FollowUpRetry,
            OriginScheduledExecutionId = origin,
            FollowUpOrdinal = nextOrdinal,
            CreatedAtUtc = _time.GetUtcNow(),
        };

        try
        {
            await _pendingStore.AddAsync(pending, CancellationToken.None).ConfigureAwait(false);
            JobLog.FollowUpQueued(_logger, nextOrdinal, policy.MaxAttempts, delay, dueAt);
            _metrics.FollowUpQueued(_def.JobId);

            // The loop decided what to wait for before this row existed, so it has to be told.
            SignalWake();
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(_logger, ex, "QueueFollowUpOccurrence");
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
                else if (task.IsFaulted)
                {
                    // JobRunner is built never to throw, so reaching here means the engine itself
                    // has a bug. Dropping the task would hide it as an unobserved exception —
                    // exactly the failure mode this library exists to remove.
                    JobLog.RunnerFaulted(_logger, task.Exception!, _def.JobId);
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

            StartRun(occurrence, TriggerManual, request.ExecutionId, followUpOrdinal: 0,
                originScheduledExecutionId: null, claimed: null, stoppingToken);
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
