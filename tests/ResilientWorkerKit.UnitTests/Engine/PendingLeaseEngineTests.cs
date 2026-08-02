using ResilientWorkerKit.Stores;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

/// <summary>
/// Loop-level behaviour of the pending-occurrence lease: crash recovery through expiry,
/// re-delivery semantics, and the completion rules that keep a follow-up chain from being
/// lost. Everything runs with yielding stores and awaiting bodies — the same discipline as
/// <see cref="FollowUpSchedulingTests"/> — so nothing here depends on a store completing
/// synchronously.
/// </summary>
public class PendingLeaseEngineTests
{
    private const string JobId = "test-job";
    private static readonly DateTimeOffset T0 = LoopHarness.T0;

    /// <summary>A schedule far in the future, so only the pending queue drives the loop.</summary>
    private static JobDefinition QuietDefinition(Action<JobBuilder<DelegateJob>>? extra = null)
        => RunnerHarness.Definition(b =>
        {
            b.AtTimes(T0.AddDays(30)).WithRetryCount(0);
            extra?.Invoke(b);
        });

    [Fact]
    public async Task ExpiredLeaseFromADeadHost_IsTakenOverAndExecuted()
    {
        // The crash-window scenario the lease model exists for: a previous process acquired
        // the row and died before any execution outcome was recorded. Under claim-as-delete
        // this occurrence was gone for good; under the lease it must re-deliver.
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Yield();
        }, yieldingStores: true);

        await harness.SeedPendingAsync(
            JobId, "planned-action", dueAtUtc: T0.AddMinutes(-2),
            leaseOwner: "dead-host", leaseAcquiredAtUtc: T0.AddMinutes(-10)); // expired at T0-5

        harness.StartLoop(QuietDefinition());

        await harness.WaitUntilAsync(async () => await harness.CountAsync(JobId, "follow-up") >= 1);
        await harness.WaitUntilAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) == 0);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RowLeasedByALiveOwner_IsNotExecuted_UntilTheLeaseExpires()
    {
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Yield();
        }, yieldingStores: true);

        // Due already, but another owner holds the lease until T0+10m.
        await harness.SeedPendingAsync(
            JobId, "planned-action", dueAtUtc: T0.AddMinutes(-1),
            leaseOwner: "other-host", leaseAcquiredAtUtc: T0.AddMinutes(5));

        harness.StartLoop(QuietDefinition());

        await harness.AssertNotHappeningAsync(async () => attempts > 0);

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => await harness.CountAsync(JobId, "follow-up") >= 1);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancelledRun_LeavesTheRowAcquirable_ForTheNextHost()
    {
        // Cancellation (a graceful shutdown) is non-terminal for a planned action: the work
        // did not happen, so the row must survive and shed its lease.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new LoopHarness(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, yieldingStores: true);

        await harness.SeedPendingAsync(JobId, "planned-action", dueAtUtc: T0.AddMinutes(-1));

        harness.StartLoop(QuietDefinition());
        await started.Task;

        await harness.StopAsync(); // cancels the run mid-flight

        await harness.WaitUntilAsync(async () =>
        {
            var next = await harness.PendingOccurrences.GetNextAsync(JobId, harness.Time.GetUtcNow());
            return next is { LeaseOwner: null, LeaseExpiresAtUtc: null };
        });
        Assert.Equal(1, await harness.PendingOccurrences.CountAsync(JobId));
    }

    [Fact]
    public async Task FollowUpPlanningFailure_KeepsTheRow_AndRedeliveryRePlans()
    {
        // The one blocking finding of the design review: if the next follow-up cannot be
        // written durably, completing the current row would end the chain silently. The row
        // must survive, re-deliver at the same ordinal, and re-plan idempotently.
        var failingStore = new FailingAddPendingStore();
        await using var harness = new LoopHarness(async (_, _) =>
        {
            await Task.Yield();
            throw new TransientJobException("still down");
        },
        yieldingStores: true,
        wrapPendingStore: inner => failingStore.Wrap(inner));

        harness.StartLoop(RunnerHarness.Definition(b => b
            .AtTimes(T0.AddMinutes(-1))
            .WithRetryCount(0)
            .RetryLater(o =>
            {
                o.MaxAttempts = 3;
                o.Delay = TimeSpan.FromMinutes(5);
                o.MaxDelay = TimeSpan.FromMinutes(5);
            })));

        // Origin fails; follow-up 1 is planned normally.
        await harness.WaitUntilAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) >= 1);

        // Break the store's AddAsync, then let follow-up 1 run and fail: planning follow-up 2
        // now cannot write, so the loop must NOT complete follow-up 1's row.
        failingStore.FailAdds = true;
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => await harness.CountAsync(JobId) >= 2);

        var retained = await harness.PendingOccurrences.GetNextAsync(JobId, harness.Time.GetUtcNow());
        Assert.NotNull(retained);
        Assert.Equal(1, retained.FollowUpOrdinal);

        // Store heals; the lease lapses; the same ordinal re-delivers, re-plans follow-up 2,
        // and only then is the follow-up-1 row completed.
        failingStore.FailAdds = false;
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () =>
        {
            var next = await harness.PendingOccurrences.GetNextAsync(JobId, harness.Time.GetUtcNow());
            return next is { FollowUpOrdinal: 2 };
        });

        Assert.True(await harness.CountAsync(JobId) >= 3, "the retained row must have re-executed");
    }

    [Fact]
    public async Task StaleRowForACompletedOccurrence_IsRemovedWithoutRunning()
    {
        // Crash after the completed record but before CompleteAsync: on the next start the
        // occurrence is already completed, so the row is cleaned up, never re-run.
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Yield();
        }, yieldingStores: true);

        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(-3), JobExecutionStatus.Completed, "follow-up", "planned-action");
        await harness.SeedPendingAsync(JobId, "planned-action", dueAtUtc: T0.AddMinutes(-3));

        harness.StartLoop(QuietDefinition());

        await harness.WaitUntilAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) == 0);
        Assert.Equal(0, attempts);
        Assert.Single(await harness.RecordsAsync(JobId)); // only the seeded record
    }

    [Fact]
    public async Task DeclinedJobLock_ReleasesTheLease_AndRunsAfterTheLockFrees()
    {
        // The job lock is unavailable (in production: held elsewhere, e.g. by another process
        // through a shared lock provider). The pending occurrence must not be lost to that:
        // the lease is released, the loop backs off, and the occurrence runs once the lock
        // frees — 1.1.x deleted the row before discovering the lock and re-inserted it, which
        // had its own crash window.
        var lockProvider = new DenyingLockProvider { Deny = true };
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Yield();
        }, yieldingStores: true, lockProvider: lockProvider);

        await harness.SeedPendingAsync(JobId, "planned-action", dueAtUtc: T0.AddMinutes(-1));

        harness.StartLoop(QuietDefinition());

        // While the lock is denied: no execution, and the row is back in the queue unleased.
        await harness.AssertNotHappeningAsync(async () => attempts > 0);
        Assert.Equal(1, await harness.PendingOccurrences.CountAsync(JobId));
        await harness.WaitUntilAsync(async () =>
        {
            var next = await harness.PendingOccurrences.GetNextAsync(JobId, harness.Time.GetUtcNow());
            return next is { LeaseOwner: null };
        });

        // Lock frees; after the decline cooldown the occurrence runs and the queue drains.
        lockProvider.Deny = false;
        await harness.AdvanceUntilAsync(TimeSpan.FromSeconds(1),
            async () => attempts >= 1 && await harness.PendingOccurrences.CountAsync(JobId) == 0);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Heartbeat_KeepsTheLeaseAlive_ThroughARunLongerThanTheLease()
    {
        var finishRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await finishRun.Task.WaitAsync(ct);
        }, yieldingStores: true);

        harness.Options.PendingOccurrenceLeaseDuration = TimeSpan.FromMinutes(2);
        await harness.SeedPendingAsync(JobId, "planned-action", dueAtUtc: T0.AddMinutes(-1));

        harness.StartLoop(QuietDefinition());
        await harness.WaitUntilAsync(() => Task.FromResult(attempts == 1));

        // Ten virtual minutes — five times the lease — pass while the run is still going. The
        // renewal heartbeat must keep the row from ever re-delivering under its own execution.
        for (var i = 0; i < 20; i++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(15);
            Assert.Equal(1, attempts);
        }

        finishRun.SetResult();
        await harness.WaitUntilAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) == 0);
        Assert.Equal(1, attempts);
    }

    // ---- ContinueAfterAbandoned (D-002) ---------------------------------------------------

    private static JobDefinition ResumingDefinition(bool optIn = true, int maxAttempts = 3)
        => RunnerHarness.Definition(b => b
            .AtTimes(T0.AddDays(30))
            .WithRetryCount(0)
            .RetryLater(o =>
            {
                o.MaxAttempts = maxAttempts;
                o.Delay = TimeSpan.FromMinutes(5);
                o.MaxDelay = TimeSpan.FromMinutes(5);
                o.ContinueAfterAbandoned = optIn;
            }));

    [Fact]
    public async Task AbandonedOrigin_ResumesTheChain_WhenOptedIn()
    {
        // The origin died mid-run (crash), so no follow-up was ever planned. With the opt-in,
        // recovery queues follow-up 1 — and it runs as a follow-up, not as a re-run of the origin.
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Yield();
        }, yieldingStores: true);

        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(-30), JobExecutionStatus.Abandoned, "schedule",
            "at:2026-08-01T09:30:00Z", JobFailureKind.Abandoned);

        harness.StartLoop(ResumingDefinition());

        await harness.WaitUntilAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) >= 1);
        var queued = await harness.PendingOccurrences.GetNextAsync(JobId, harness.Time.GetUtcNow());
        Assert.Equal("at:2026-08-01T09:30:00Z+followup-1", queued!.IdentityToken);
        Assert.Equal(1, queued.FollowUpOrdinal);

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => await harness.CountAsync(JobId, "follow-up") >= 1);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AbandonedOrigin_IsNotResumed_ByDefault()
    {
        // Off by default: the abandoned run may have completed its side effect with the
        // response unobserved, and only an idempotent body makes re-execution safe.
        await using var harness = new LoopHarness(async (_, _) => await Task.Yield(), yieldingStores: true);

        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(-30), JobExecutionStatus.Abandoned, "schedule",
            "at:2026-08-01T09:30:00Z", JobFailureKind.Abandoned);

        harness.StartLoop(ResumingDefinition(optIn: false));

        await harness.AssertNotHappeningAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) > 0);
    }

    [Fact]
    public async Task PermanentlyFailedOrigin_IsNotResumed()
    {
        await using var harness = new LoopHarness(async (_, _) => await Task.Yield(), yieldingStores: true);

        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(-30), JobExecutionStatus.Failed, "schedule",
            "at:2026-08-01T09:30:00Z", JobFailureKind.Permanent);

        harness.StartLoop(ResumingDefinition());

        await harness.AssertNotHappeningAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) > 0);
    }

    [Fact]
    public async Task ChainThatAlreadyAdvanced_IsNotResumedAgain()
    {
        // Follow-up 1 already has an execution record, so the chain advanced on its own; the
        // scan must not restart it from ordinal 1.
        await using var harness = new LoopHarness(async (_, _) => await Task.Yield(), yieldingStores: true);

        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(-30), JobExecutionStatus.Abandoned, "schedule",
            "at:2026-08-01T09:30:00Z", JobFailureKind.Abandoned);
        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(-25), JobExecutionStatus.Failed, "follow-up",
            "at:2026-08-01T09:30:00Z+followup-1", JobFailureKind.Transient);

        harness.StartLoop(ResumingDefinition());

        await harness.AssertNotHappeningAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) > 0);
    }
}

/// <summary>Denies the job lock while <see cref="Deny"/> is set (a lock held elsewhere).</summary>
internal sealed class DenyingLockProvider : IJobLockProvider
{
    private readonly InProcessJobLockProvider _inner = new();

    public bool Deny { get; set; }

    public Task<IAsyncDisposable?> TryAcquireAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken = default)
        => Deny
            ? Task.FromResult<IAsyncDisposable?>(null)
            : _inner.TryAcquireAsync(jobId, timeout, cancellationToken);
}

/// <summary>Fault injection: fails every AddAsync while <see cref="FailAdds"/> is set.</summary>
internal sealed class FailingAddPendingStore : IPendingOccurrenceStore
{
    private IPendingOccurrenceStore _inner = null!;

    public bool FailAdds { get; set; }

    public IPendingOccurrenceStore Wrap(IPendingOccurrenceStore inner)
    {
        _inner = inner;
        return this;
    }

    public async Task<bool> AddAsync(PendingOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return FailAdds
            ? throw new InvalidOperationException("Injected store failure.")
            : await _inner.AddAsync(occurrence, cancellationToken);
    }

    public Task<PendingOccurrence?> GetNextAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => _inner.GetNextAsync(jobId, nowUtc, cancellationToken);

    public Task<string?> TryAcquireLeaseAsync(string id, string owner, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => _inner.TryAcquireLeaseAsync(id, owner, duration, nowUtc, cancellationToken);

    public Task<bool> TryRenewLeaseAsync(string id, string leaseToken, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => _inner.TryRenewLeaseAsync(id, leaseToken, duration, nowUtc, cancellationToken);

    public Task<bool> CompleteAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
        => _inner.CompleteAsync(id, leaseToken, cancellationToken);

    public Task<bool> ReleaseAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
        => _inner.ReleaseAsync(id, leaseToken, cancellationToken);

    public Task<int> CountAsync(string jobId, CancellationToken cancellationToken = default)
        => _inner.CountAsync(jobId, cancellationToken);
}
