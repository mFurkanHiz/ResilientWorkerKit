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
    bool IsOutOfBand = false)
{
    /// <summary>
    /// When the loop may act on this work. Schedule work is actionable at its occurrence time.
    /// Pending work can be pushed later without changing when it was <em>due</em>: until
    /// another owner's lease expires, or until a decline cooldown ends.
    /// </summary>
    public DateTimeOffset EffectiveDueAtUtc { get; init; } = Occurrence.ScheduledAtUtc;
}

/// <summary>
/// Proof of ownership of one pending occurrence for the duration of its execution. The token
/// is known only to this loop; renew, complete and release are conditional on it at the store.
/// </summary>
internal sealed record OccurrenceLease(string Id, string Token, string IdentityToken);

/// <summary>Durable outcome of planning a follow-up after a failed execution.</summary>
internal enum FollowUpPlanOutcome
{
    /// <summary>No follow-up was called for (success, policy absent, kind excluded, chain exhausted).</summary>
    NotNeeded,

    /// <summary>The next follow-up exists durably — written now, or already present.</summary>
    Planned,

    /// <summary>
    /// A follow-up was called for but could not be written durably. The caller must not
    /// complete the current occurrence row: keeping it lets the lease lapse and the
    /// occurrence re-deliver, which re-plans idempotently — a duplicate run is the accepted
    /// at-least-once corner, a lost chain is not.
    /// </summary>
    WriteFailed,
}

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
    private readonly WorkerKitOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Retry cadence after a pending-store operation failed (see the backoff fields).</summary>
    private static readonly TimeSpan StoreRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// UTC ticks until which pending work is not re-attempted — set when the runner declined
    /// the job lock and when a pending-store write threw. Without it, a due row plus a failing
    /// acquire (or a held lock) would make compute-fire-recompute spin hot, hammering the
    /// store for the whole duration of an outage.
    /// </summary>
    private long _pendingBackoffUntilTicks;

    /// <summary>
    /// UTC ticks of a forced wake-up, or 0. Set when reading the pending queue failed — the
    /// loop must not conclude "queue empty" from an outage and sleep forever over durable
    /// work — and consulted by <see cref="BuildDelay"/> alongside the flush schedule below.
    /// </summary>
    private long _pendingRecheckAtTicks;

    /// <summary>
    /// Follow-ups that could not be written durably by an origin run (no row, no lease — the
    /// row-retention protection does not apply). Kept in-process and re-attempted with backoff
    /// until the write succeeds; the unique (JobId, IdentityToken) index makes re-attempts
    /// idempotent. If the process dies first, the opt-in ContinueAfterAbandoned scan is the
    /// remaining net, which is documented.
    /// </summary>
    private readonly List<PendingOccurrence> _unplannedFollowUps = new();
    private long _nextUnplannedFlushTicks;
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
        WorkerKitOptions options,
        TimeProvider time,
        ILogger logger)
    {
        _def = definition;
        _runner = runner;
        _executionStore = executionStore;
        _pendingStore = pendingStore;
        _health = health;
        _metrics = metrics;
        _options = options;
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
                originScheduledExecutionId: null, lease: null, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HarvestFinishedRuns();
            await FlushUnplannedFollowUpsAsync(stoppingToken).ConfigureAwait(false);

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
        // A forced wake (pending re-read after a failed read, or a flush re-attempt) bounds
        // every sleep below, including the "nothing to do" and "wait for capacity" ones:
        // recovery work must not depend on an unrelated event happening to wake the loop.
        var forcedWake = NextForcedWakeUtc();

        if (next is not { } work)
        {
            return forcedWake is { } wake
                ? DelayUntilAsync(wake, token)
                : Task.Delay(Timeout.InfiniteTimeSpan, _time, token);
        }

        if (atCapacity && work.IsOutOfBand && work.EffectiveDueAtUtc <= _time.GetUtcNow())
        {
            return forcedWake is { } wake
                ? DelayUntilAsync(wake, token)
                : Task.Delay(Timeout.InfiniteTimeSpan, _time, token);
        }

        var target = work.EffectiveDueAtUtc;
        if (forcedWake is { } cap && cap < target)
        {
            target = cap;
        }

        return DelayUntilAsync(target, token);
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

            if (_def.FollowUpRetry is { ContinueAfterAbandoned: true } policy)
            {
                await ResumeInterruptedChainsAsync(recent, policy, cancellationToken).ConfigureAwait(false);
            }
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
    /// Opt-in crash recovery for follow-up chains (<see cref="FollowUpRetryOptions.ContinueAfterAbandoned"/>).
    /// Only <em>origin</em> executions need this: a follow-up execution is backed by a durable
    /// row whose lease re-delivers it, but an origin that died mid-run — or crashed between its
    /// failure record and the first follow-up's durable write — left no row behind, so its
    /// chain never started. Classification is by trigger type, which is engine-assigned; a
    /// custom schedule's identity tokens are never parsed for this. Scope is bounded by the
    /// same recent-execution window the anchor recovery uses.
    /// </summary>
    private async Task ResumeInterruptedChainsAsync(
        IReadOnlyList<JobExecutionRecord> recent,
        FollowUpRetryOptions policy,
        CancellationToken cancellationToken)
    {
        var interrupted = recent
            .Where(r => r.TriggerType != TriggerFollowUp)
            .GroupBy(r => r.ScheduledExecutionId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.StartedAtUtc).First())
            .Where(r => r.Status is JobExecutionStatus.Abandoned or JobExecutionStatus.Failed);

        foreach (var record in interrupted)
        {
            // Same gate as in-process planning: a deterministic failure repeats.
            if (!policy.RetryPermanentFailures
                && record.FailureKind is JobFailureKind.Permanent or JobFailureKind.Misconfigured)
            {
                continue;
            }

            var originIdentity = record.ScheduledExecutionId.StartsWith(_def.JobId + ":", StringComparison.Ordinal)
                ? record.ScheduledExecutionId[(_def.JobId.Length + 1)..]
                : record.ScheduledExecutionId;

            // If follow-up 1 ever ran, the chain advanced on its own; nothing to resume.
            bool chainAdvanced;
            try
            {
                chainAdvanced = await _executionStore.ExistsForScheduledExecutionAsync(
                        _def.JobId, $"{_def.JobId}:{originIdentity}+followup-1", completedOnly: false,
                        cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail safe: not resuming is recoverable on the next start; blind queueing
                // against an unreadable store is not.
                JobLog.StoreOperationFailed(_logger, ex, "CheckFollowUpChain");
                continue;
            }

            if (chainAdvanced)
            {
                continue;
            }

            var now = _time.GetUtcNow();
            var dueAt = now + policy.DelayFor(1);
            var added = false;
            try
            {
                added = await _pendingStore.AddAsync(
                    new PendingOccurrence
                    {
                        Id = Guid.NewGuid().ToString("n"),
                        JobId = _def.JobId,
                        DueAtUtc = dueAt,
                        IdentityToken = $"{originIdentity}+followup-1",
                        Source = PendingOccurrenceSources.FollowUpRetry,
                        OriginScheduledExecutionId = record.ScheduledExecutionId,
                        FollowUpOrdinal = 1,
                        CreatedAtUtc = now,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                JobLog.StoreOperationFailed(_logger, ex, "ResumeFollowUpChain");
            }

            if (added)
            {
                JobLog.FollowUpChainResumed(_logger, record.ScheduledExecutionId, record.Status, dueAt);
                _metrics.FollowUpQueued(_def.JobId);
                SignalWake();
            }
            else
            {
                JobLog.PendingOccurrenceAlreadyQueued(_logger, $"{originIdentity}+followup-1");
            }
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

        // When the loop may act on the pending occurrence: not before it is due; not before
        // another owner's unexpired lease would lapse; not before a decline cooldown ends.
        var now = _time.GetUtcNow();
        var actionableAt = pending.DueAtUtc;
        if (pending.LeaseExpiresAtUtc is { } leaseExpiry && leaseExpiry >= now && leaseExpiry > actionableAt)
        {
            actionableAt = leaseExpiry;
        }

        var backoffUntil = new DateTimeOffset(Volatile.Read(ref _pendingBackoffUntilTicks), TimeSpan.Zero);
        if (backoffUntil > actionableAt)
        {
            actionableAt = backoffUntil;
        }

        if (scheduled is not null && scheduled.Occurrence.ScheduledAtUtc <= actionableAt)
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
            IsOutOfBand: true)
        {
            EffectiveDueAtUtc = actionableAt,
        };
    }

    private async Task<PendingOccurrence?> GetNextPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var next = await _pendingStore.GetNextAsync(_def.JobId, _time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _pendingRecheckAtTicks, 0);
            return next;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Null must not mean "queue empty" here: with no schedule the loop would sleep
            // forever over durable work it merely failed to see. Force a re-read.
            JobLog.StoreOperationFailed(_logger, ex, "GetNextPendingOccurrence");
            Volatile.Write(ref _pendingRecheckAtTicks, (_time.GetUtcNow() + StoreRetryDelay).UtcTicks);
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
        // next scheduled occurrence is due. And the anchor never moves BACKWARDS: an occurrence
        // that waited in the overlap queue refires after later occurrences already advanced the
        // phase, and regressing it would re-derive those — occurrences the overlap policy
        // already recorded as skipped — as brand-new misfires.
        if (!work.IsOutOfBand && (_anchorUtc is not { } anchor || occurrence.ScheduledAtUtc > anchor))
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

            // A pending row for an occurrence that already completed is stale — for example a
            // crash landed between the terminal record and CompleteAsync. Remove it under a
            // lease of our own, or the loop would surface it forever.
            await RemoveStalePendingRowAsync(work.Pending, stoppingToken).ConfigureAwait(false);
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

        // Lease last, after every check that could still decide not to run. Unlike 1.x's
        // claim-as-delete, the lease is revocable: if this process dies before an execution
        // outcome exists durably, the lease expires and the occurrence re-delivers.
        OccurrenceLease? lease = null;
        if (work.Pending is { } pending)
        {
            string? token;
            try
            {
                token = await _pendingStore.TryAcquireLeaseAsync(
                    pending.Id, _options.HostInstanceId, _options.PendingOccurrenceLeaseDuration,
                    _time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Back off: the row is due and unleased, so without this the loop would
                // recompute and re-throw in a hot spin for the whole store outage.
                JobLog.StoreOperationFailed(_logger, ex, "AcquirePendingLease");
                ApplyPendingBackoff(StoreRetryDelay);
                return;
            }

            if (token is null)
            {
                // Another owner holds it; the loop recomputes and sleeps until that lease
                // could expire.
                JobLog.PendingLeaseNotAcquired(_logger, pending.IdentityToken);
                return;
            }

            lease = new OccurrenceLease(pending.Id, token, pending.IdentityToken);
            JobLog.FollowUpStarting(_logger, pending.FollowUpOrdinal, pending.OriginScheduledExecutionId);
        }

        StartRun(occurrence, trigger, presetExecutionId: null, work.Pending?.FollowUpOrdinal ?? 0,
            work.Pending?.OriginScheduledExecutionId, lease, stoppingToken);
    }

    /// <summary>
    /// Removes a pending row whose occurrence already has a completed execution. Best effort:
    /// on any failure the row stays leased or intact, and a later iteration retries.
    /// </summary>
    private async Task RemoveStalePendingRowAsync(PendingOccurrence? pending, CancellationToken stoppingToken)
    {
        if (pending is null)
        {
            return;
        }

        try
        {
            var token = await _pendingStore.TryAcquireLeaseAsync(
                pending.Id, _options.HostInstanceId, _options.PendingOccurrenceLeaseDuration,
                _time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            if (token is null)
            {
                return; // another owner has it; they will reach the same conclusion
            }

            if (await _pendingStore.CompleteAsync(pending.Id, token, stoppingToken).ConfigureAwait(false))
            {
                JobLog.StalePendingOccurrenceRemoved(_logger, pending.IdentityToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Same hot-spin shape as a failing acquire: the stale row stays surfaced and due.
            JobLog.StoreOperationFailed(_logger, ex, "RemoveStalePendingOccurrence");
            ApplyPendingBackoff(StoreRetryDelay);
        }
    }

    private void StartRun(
        JobScheduleOccurrence occurrence,
        string trigger,
        string? presetExecutionId,
        int followUpOrdinal,
        string? originScheduledExecutionId,
        OccurrenceLease? lease,
        CancellationToken stoppingToken)
    {
        var runTask = RunAndPlanFollowUpAsync(
            occurrence, trigger, presetExecutionId, followUpOrdinal, originScheduledExecutionId,
            lease, stoppingToken);
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
        OccurrenceLease? lease,
        CancellationToken stoppingToken)
    {
        var runnerTask = _runner.RunAsync(_def, occurrence, trigger, presetExecutionId, _logger, stoppingToken);
        if (lease is not null)
        {
            // Heartbeat: a live run's lease must never expire under it, or a second host would
            // start the same occurrence mid-run. Renewal stops when the run completes.
            await RenewLeaseWhileRunningAsync(runnerTask, lease).ConfigureAwait(false);
        }

        var result = await runnerTask.ConfigureAwait(false);

        if (result is null)
        {
            // The runner declined — the job lock was unavailable. Release the lease so the
            // occurrence is immediately acquirable, and back off so acquire/decline/release
            // cannot spin against a held lock.
            ApplyDeclineCooldown();
            await ReleaseLeaseAsync(lease, "job lock unavailable").ConfigureAwait(false);
            return null;
        }

        if (result.Status == JobExecutionStatus.Cancelled)
        {
            // Cancellation is non-terminal for a planned action: the work did not happen.
            // Leave the row and release the lease, so the occurrence re-delivers here after
            // the next start — or on another host.
            await ReleaseLeaseAsync(lease, "execution cancelled").ConfigureAwait(false);
            return result;
        }

        var planOutcome = await PlanFollowUpIfNeededAsync(
            result, occurrence, followUpOrdinal, originScheduledExecutionId,
            stashOnWriteFailure: lease is null).ConfigureAwait(false);

        if (lease is not null)
        {
            if (planOutcome == FollowUpPlanOutcome.WriteFailed)
            {
                // The chain's next link is not durable, so this row must survive: the lease
                // lapses, the occurrence re-delivers at the same ordinal, and re-planning is
                // idempotent through the unique (JobId, IdentityToken) index.
                JobLog.FollowUpWriteFailedRowRetained(_logger, followUpOrdinal + 1);
            }
            else
            {
                await CompleteLeaseAsync(lease).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>Renews the lease at a third of its duration until the run completes.</summary>
    private async Task RenewLeaseWhileRunningAsync(Task<JobRunResult?> runnerTask, OccurrenceLease lease)
    {
        var interval = TimeSpan.FromTicks(Math.Max(1, _options.PendingOccurrenceLeaseDuration.Ticks / 3));

        while (!runnerTask.IsCompleted)
        {
            using var delayCts = new CancellationTokenSource();
            var delay = Task.Delay(interval, _time, delayCts.Token);
            var finished = await Task.WhenAny(runnerTask, delay).ConfigureAwait(false);
            if (finished != delay)
            {
                await delayCts.CancelAsync().ConfigureAwait(false);
                await ObserveAsync(delay).ConfigureAwait(false);
                break;
            }

            try
            {
                var renewed = await _pendingStore.TryRenewLeaseAsync(
                    lease.Id, lease.Token, _options.PendingOccurrenceLeaseDuration, _time.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
                if (!renewed)
                {
                    // The lease is gone for good (taken over, or the row was removed). The run
                    // is not killed: cancelling cannot undo side effects already performed, and
                    // the duplicate-execution corner is the documented at-least-once trade.
                    JobLog.PendingLeaseLost(_logger, lease.IdentityToken);
                    break;
                }
            }
            catch (Exception ex)
            {
                // Transient store trouble: keep the run going and try again next tick.
                JobLog.StoreOperationFailed(_logger, ex, "RenewPendingLease");
            }
        }
    }

    private async Task ReleaseLeaseAsync(OccurrenceLease? lease, string reason)
    {
        if (lease is null)
        {
            return;
        }

        try
        {
            if (await _pendingStore.ReleaseAsync(lease.Id, lease.Token, CancellationToken.None).ConfigureAwait(false))
            {
                JobLog.OutOfBandWorkReturnedToQueue(_logger, lease.IdentityToken, reason);
                SignalWake();
            }
        }
        catch (Exception ex)
        {
            // The lease simply lapses instead; recovery is slower but nothing is lost.
            JobLog.StoreOperationFailed(_logger, ex, "ReleasePendingLease");
        }
    }

    private async Task CompleteLeaseAsync(OccurrenceLease lease)
    {
        try
        {
            if (!await _pendingStore.CompleteAsync(lease.Id, lease.Token, CancellationToken.None).ConfigureAwait(false))
            {
                // We no longer held the lease. If the row still exists it belongs to another
                // owner now and will re-deliver; the completed-identity check contains the blast
                // radius to one duplicate run at most.
                JobLog.PendingLeaseLost(_logger, lease.IdentityToken);
            }
        }
        catch (Exception ex)
        {
            // The row stays leased until expiry, then re-delivers; the stale-row cleanup in
            // FireOccurrenceAsync removes it once the completed record is visible.
            JobLog.StoreOperationFailed(_logger, ex, "CompletePendingOccurrence");
        }
    }

    private void ApplyDeclineCooldown()
    {
        var cooldown = _options.LockAcquireTimeout >= TimeSpan.FromSeconds(1)
            ? _options.LockAcquireTimeout
            : TimeSpan.FromSeconds(1);
        ApplyPendingBackoff(cooldown);
    }

    /// <summary>Defers the next attempt on pending work; never shortens an existing backoff.</summary>
    private void ApplyPendingBackoff(TimeSpan duration)
    {
        var until = (_time.GetUtcNow() + duration).UtcTicks;
        if (until > Volatile.Read(ref _pendingBackoffUntilTicks))
        {
            Volatile.Write(ref _pendingBackoffUntilTicks, until);
        }
    }

    /// <summary>
    /// The earliest instant the loop must wake regardless of schedule and queue state: a
    /// pending-queue re-read after a failed read, or a re-attempt of follow-ups that could
    /// not be written durably. Null when neither applies.
    /// </summary>
    private DateTimeOffset? NextForcedWakeUtc()
    {
        var recheck = Volatile.Read(ref _pendingRecheckAtTicks);

        long flush = 0;
        lock (_unplannedFollowUps)
        {
            if (_unplannedFollowUps.Count > 0)
            {
                flush = Math.Max(_nextUnplannedFlushTicks, 1);
            }
        }

        var earliest = (recheck, flush) switch
        {
            (0, 0) => 0,
            (0, _) => flush,
            (_, 0) => recheck,
            _ => Math.Min(recheck, flush),
        };

        return earliest == 0 ? null : new DateTimeOffset(earliest, TimeSpan.Zero);
    }

    /// <summary>
    /// Re-attempts durable writes for follow-ups an origin run failed to queue. Runs at the
    /// top of every loop iteration; a failure backs off <see cref="StoreRetryDelay"/> rather
    /// than hammering a store that is already in trouble.
    /// </summary>
    private async Task FlushUnplannedFollowUpsAsync(CancellationToken cancellationToken)
    {
        List<PendingOccurrence> snapshot;
        lock (_unplannedFollowUps)
        {
            if (_unplannedFollowUps.Count == 0
                || _time.GetUtcNow().UtcTicks < _nextUnplannedFlushTicks)
            {
                return;
            }

            snapshot = _unplannedFollowUps.ToList();
        }

        foreach (var pending in snapshot)
        {
            try
            {
                // false means the row already exists (a re-attempt raced something) — flushed
                // either way.
                if (await _pendingStore.AddAsync(pending, cancellationToken).ConfigureAwait(false))
                {
                    var policy = _def.FollowUpRetry;
                    JobLog.FollowUpQueued(
                        _logger, pending.FollowUpOrdinal, policy?.MaxAttempts ?? pending.FollowUpOrdinal,
                        TimeSpan.Zero, pending.DueAtUtc);
                    _metrics.FollowUpQueued(_def.JobId);
                }
                else
                {
                    JobLog.PendingOccurrenceAlreadyQueued(_logger, pending.IdentityToken);
                }

                lock (_unplannedFollowUps)
                {
                    _unplannedFollowUps.Remove(pending);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                JobLog.StoreOperationFailed(_logger, ex, "FlushUnplannedFollowUp");
                lock (_unplannedFollowUps)
                {
                    _nextUnplannedFlushTicks = (_time.GetUtcNow() + StoreRetryDelay).UtcTicks;
                }

                return;
            }
        }
    }

    private void StashUnplannedFollowUp(PendingOccurrence pending)
    {
        lock (_unplannedFollowUps)
        {
            _unplannedFollowUps.Add(pending);
        }

        SignalWake();
    }

    /// <param name="result">The terminal outcome of the run that may need a follow-up.</param>
    /// <param name="occurrence">The occurrence that ran.</param>
    /// <param name="followUpOrdinal">This run's ordinal (0 for an origin run).</param>
    /// <param name="originScheduledExecutionId">The chain's origin, when this run was a follow-up.</param>
    /// <param name="stashOnWriteFailure">
    /// True for origin runs, which have no durable row: a failed write is kept in-process and
    /// re-attempted with backoff. Leased runs pass false — their protection is keeping the
    /// current row, which re-delivers durably.
    /// </param>
    private async Task<FollowUpPlanOutcome> PlanFollowUpIfNeededAsync(
        JobRunResult result,
        JobScheduleOccurrence occurrence,
        int followUpOrdinal,
        string? originScheduledExecutionId,
        bool stashOnWriteFailure)
    {
        if (_def.FollowUpRetry is not { } policy || result.Status != JobExecutionStatus.Failed)
        {
            return FollowUpPlanOutcome.NotNeeded;
        }

        // A deterministic failure normally repeats; retrying it just burns the window unless the
        // job opts in.
        if (!policy.RetryPermanentFailures
            && result.FailureKind is JobFailureKind.Permanent or JobFailureKind.Misconfigured)
        {
            JobLog.FollowUpSkippedForPermanentFailure(_logger, result.FailureKind.Value);
            return FollowUpPlanOutcome.NotNeeded;
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
            return FollowUpPlanOutcome.NotNeeded;
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
            if (await _pendingStore.AddAsync(pending, CancellationToken.None).ConfigureAwait(false))
            {
                JobLog.FollowUpQueued(_logger, nextOrdinal, policy.MaxAttempts, delay, dueAt);
                _metrics.FollowUpQueued(_def.JobId);
            }
            else
            {
                // A re-delivered occurrence re-ran and re-planned; the database's uniqueness
                // guarantee turned the second write into a no-op — which is the design.
                JobLog.PendingOccurrenceAlreadyQueued(_logger, pending.IdentityToken);
            }

            // The loop decided what to wait for before this row existed, so it has to be told.
            SignalWake();
            return FollowUpPlanOutcome.Planned;
        }
        catch (Exception ex)
        {
            JobLog.StoreOperationFailed(_logger, ex, "QueueFollowUpOccurrence");
            if (stashOnWriteFailure)
            {
                // No row backs this chain yet, so nothing durable would retry the write. Keep
                // it in-process and re-attempt with backoff (idempotent via the unique index).
                StashUnplannedFollowUp(pending);
            }

            return FollowUpPlanOutcome.WriteFailed;
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
                originScheduledExecutionId: null, lease: null, stoppingToken);
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
