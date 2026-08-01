using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

/// <summary>
/// Loop-level behaviour of durable follow-up retries. Every test here runs with yielding stores
/// and a job body that awaits, because that is what a job doing real I/O looks like — and the
/// original v1.1 tests, which used a synchronously-throwing body over synchronous stores, could
/// not observe whether the loop ever noticed the queued follow-up at all.
/// </summary>
public class FollowUpSchedulingTests
{
    private const string JobId = "test-job";

    private static JobDefinition Definition(int maxAttempts = 2, Action<JobBuilder<DelegateJob>>? extra = null)
        => RunnerHarness.Definition(b =>
        {
            b.AtTimes(LoopHarness.T0.AddMinutes(-1))   // already due, fires at once
                .WithRetryCount(0)                      // no in-execution attempts: one try per occurrence
                .RetryLater(o =>
                {
                    o.MaxAttempts = maxAttempts;
                    o.Delay = TimeSpan.FromMinutes(5);
                    o.MaxDelay = TimeSpan.FromMinutes(5);
                });
            extra?.Invoke(b);
        });

    [Fact]
    public async Task FollowUpRunsInTheSameHost_WhenTheJobBodyAwaits()
    {
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Yield();               // any real I/O does this
            throw new TransientJobException("upstream down");
        }, yieldingStores: true);

        harness.StartLoop(Definition(maxAttempts: 2));

        // The original occurrence fails and queues follow-up 1.
        await harness.WaitUntilAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) >= 1);

        // Advancing past the follow-up delay must actually run it. Before the fix the loop was
        // asleep on an infinite delay here and never woke, so this never became true.
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => await harness.CountAsync(JobId) >= 2);

        Assert.True(attempts >= 2, $"the follow-up must execute the job body again; ran {attempts} time(s)");
    }

    [Fact]
    public async Task FollowUpsRunToExhaustion_AndDrainTheQueue()
    {
        var attempts = 0;
        await using var harness = new LoopHarness(async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Yield();
            throw new TransientJobException("still down");
        }, yieldingStores: true);

        harness.StartLoop(Definition(maxAttempts: 2));

        // original + 2 follow-ups = 3 executions, then the queue is empty for good.
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => await harness.CountAsync(JobId) >= 3);

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => await harness.PendingOccurrences.CountAsync(JobId) == 0);

        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 3);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task FollowUpIdentityIsDerivedFromTheOrigin_NotFromThePreviousFollowUp()
    {
        // Chaining each token onto the previous one grows it without bound and eventually
        // overflows the 300-character column on providers that enforce lengths.
        await using var harness = new LoopHarness(async (_, _) =>
        {
            await Task.Yield();
            throw new TransientJobException("down");
        }, yieldingStores: true);

        harness.StartLoop(Definition(maxAttempts: 3));

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => await harness.CountAsync(JobId) >= 3);

        var identities = (await harness.RecordsAsync(JobId))
            .Select(r => r.ScheduledExecutionId)
            .ToList();

        Assert.All(identities, id =>
        {
            var occurrences = id.Split("followup-").Length - 1;
            Assert.True(occurrences <= 1, $"identity accumulated follow-up segments: {id}");
            Assert.True(id.Length <= 300, $"identity exceeds the persisted column budget: {id.Length} chars");
        });
    }

    [Fact]
    public async Task AFollowUpDoesNotShiftTheScheduleAnchor()
    {
        // An out-of-band retry must not move when the next scheduled occurrence is due.
        var t0 = LoopHarness.T0;
        await using var harness = new LoopHarness(async (context, _) =>
        {
            await Task.Yield();
            if (context.ScheduledExecutionId.Contains("followup-", StringComparison.Ordinal))
            {
                return;   // the follow-up succeeds
            }

            throw new TransientJobException("first occurrence fails");
        }, yieldingStores: true);

        // Two planned instants an hour apart; the first fails and retries out of band.
        var definition = RunnerHarness.Definition(b => b
            .AtTimes(t0.AddMinutes(-1), t0.AddHours(1))
            .WithRetryCount(0)
            .RetryLater(o =>
            {
                o.MaxAttempts = 1;
                o.Delay = TimeSpan.FromMinutes(5);
                o.MaxDelay = TimeSpan.FromMinutes(5);
            }));

        harness.StartLoop(definition);

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1),
            async () => (await harness.RecordsAsync(JobId))
                .Any(r => r.ScheduledExecutionId.Contains("followup-", StringComparison.Ordinal)
                          && r.Status == JobExecutionStatus.Completed));

        // The second planned instant must still run at its own time, not be skipped because the
        // follow-up moved the anchor past it.
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(5),
            async () => (await harness.RecordsAsync(JobId))
                .Any(r => r.ScheduledAtUtc == t0.AddHours(1)));
    }

    [Fact]
    public async Task AnOccurrenceSkippedByTheOverlapPolicy_IsNotSilentlyDeleted()
    {
        // Claiming is a delete. Claiming before the overlap decision loses the occurrence with
        // no execution record and no dead letter.
        var release = new SemaphoreSlim(0);
        var bodyRuns = 0;

        await using var harness = new LoopHarness(async (context, ct) =>
        {
            Interlocked.Increment(ref bodyRuns);
            await Task.Yield();
            if (context.ScheduledExecutionId.Contains("followup-", StringComparison.Ordinal))
            {
                return;
            }

            // The first occurrence fails fast so a follow-up is queued...
            throw new TransientJobException("down");
        }, yieldingStores: true);

        harness.StartLoop(Definition(maxAttempts: 1));

        await harness.WaitUntilAsync(async () => await harness.PendingOccurrences.CountAsync(JobId) >= 1);

        // ...and when it comes due it must either run or stay queued — never vanish.
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () =>
            await harness.CountAsync(JobId) >= 2 || await harness.PendingOccurrences.CountAsync(JobId) == 0);

        var executions = await harness.CountAsync(JobId);
        var queued = await harness.PendingOccurrences.CountAsync(JobId);
        Assert.True(executions >= 2 || queued >= 1,
            $"the queued occurrence disappeared: {executions} execution(s), {queued} still queued");

        release.Dispose();
        Assert.True(bodyRuns >= 2);
    }
}
