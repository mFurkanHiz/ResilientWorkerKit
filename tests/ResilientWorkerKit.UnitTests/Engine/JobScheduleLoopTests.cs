using ResilientWorkerKit.Engine;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

public class JobScheduleLoopTests
{
    private const string JobId = "test-job";
    private static readonly DateTimeOffset T0 = LoopHarness.T0;

    [Fact]
    public async Task IntervalOccurrences_FireOnVirtualSchedule()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        harness.StartLoop(RunnerHarness.Definition(b => b.WithInterval(TimeSpan.FromMinutes(5))));

        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 0);

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 1);
        var first = (await harness.RecordsAsync(JobId)).Single();
        Assert.Equal(T0.AddMinutes(5), first.ScheduledAtUtc);
        Assert.Equal("schedule", first.TriggerType);

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 2);
        var identities = (await harness.RecordsAsync(JobId)).Select(r => r.ScheduledExecutionId).ToList();
        Assert.Equal(2, identities.Distinct().Count());
    }

    [Fact]
    public async Task RunOnStartup_RunsImmediately_WithoutAdvancingTime()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(30))
            .RunOnStartup()));

        await harness.WaitUntilAsync(async () => await harness.CountAsync(JobId) >= 1);

        var record = (await harness.RecordsAsync(JobId)).Single();
        Assert.Equal("startup", record.TriggerType);
    }

    [Fact]
    public async Task MisfireSkip_DoesNotRunTheMissedOccurrence()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        // Last handled occurrence 12 minutes ago; occurrences at -7 and -2 minutes were missed.
        await harness.SeedExecutionAsync(JobId, T0.AddMinutes(-12));

        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(5)))); // default misfire policy: Skip

        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 1);

        // The next run is the FUTURE aligned occurrence at T0+3min — not the missed one.
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 2);
        var newest = (await harness.RecordsAsync(JobId)).First(r => r.ScheduledAtUtc > T0.AddMinutes(-12));
        Assert.Equal(T0.AddMinutes(3), newest.ScheduledAtUtc);
    }

    [Fact]
    public async Task MisfireRunImmediatelyOnce_RunsTheMostRecentMissedOccurrence_Once()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        await harness.SeedExecutionAsync(JobId, T0.AddMinutes(-12));

        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(5))
            .WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce)));

        await harness.WaitUntilAsync(async () => await harness.CountAsync(JobId, "misfire") >= 1);

        var misfireRuns = (await harness.RecordsAsync(JobId)).Where(r => r.TriggerType == "misfire").ToList();
        var run = Assert.Single(misfireRuns);
        Assert.Equal(T0.AddMinutes(-2), run.ScheduledAtUtc); // the most recent missed occurrence

        // No second misfire run appears afterwards.
        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId, "misfire") > 1);
    }

    [Fact]
    public async Task MisfireRecovery_IsRestartSafe_NeverCreatesTheSameOccurrenceTwice()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        await harness.SeedExecutionAsync(JobId, T0.AddMinutes(-12));
        // The missed occurrence at T0-2min was ALREADY attempted (and failed) before a crash:
        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(-2), JobExecutionStatus.Failed, triggerType: "misfire");

        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(5))
            .WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce)));

        // The restart must not create a third record for the same missed occurrence.
        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 2);
    }

    [Fact]
    public async Task RunIfWithinTolerance_RunsWhenLatenessIsInsideTheTolerance()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        await harness.SeedExecutionAsync(JobId, T0.AddMinutes(-12));

        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(5))
            .WithMisfirePolicy(MisfirePolicy.RunIfWithinTolerance, TimeSpan.FromMinutes(5))));

        // Missed occurrence at T0-2min is 2 minutes late — inside the 5-minute tolerance.
        await harness.WaitUntilAsync(async () => await harness.CountAsync(JobId, "misfire") >= 1);
    }

    [Fact]
    public async Task RunIfWithinTolerance_SkipsWhenTooLate()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        await harness.SeedExecutionAsync(JobId, T0.AddMinutes(-12));

        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(5))
            .WithMisfirePolicy(MisfirePolicy.RunIfWithinTolerance, TimeSpan.FromMinutes(1))));

        // 2 minutes late > 1 minute tolerance ⇒ no immediate run.
        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId, "misfire") > 0);
    }

    [Fact]
    public async Task MissedOneTimeSchedule_RunsExactlyOnce_ByDefault()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        harness.StartLoop(RunnerHarness.Definition(b => b
            .OnceAt(T0.AddHours(-1)))); // already in the past at startup

        await harness.WaitUntilAsync(async () => await harness.CountAsync(JobId) >= 1);

        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 1);
    }

    [Fact]
    public async Task FutureOneTimeSchedule_FiresOnce_ThenNeverAgain()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        harness.StartLoop(RunnerHarness.Definition(b => b.OnceAt(T0.AddMinutes(10))));

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(2), async () => await harness.CountAsync(JobId) >= 1);

        harness.Time.Advance(TimeSpan.FromHours(5));
        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 1);
    }

    [Fact]
    public async Task CompletedOccurrenceIdentity_IsNeverExecutedTwice()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        // The one-time occurrence at T0+5min already completed (e.g. before a restart).
        // Seeded as trigger "manual" so it does not act as the schedule anchor.
        await harness.SeedExecutionAsync(
            JobId, T0.AddMinutes(5), JobExecutionStatus.Completed,
            triggerType: "manual", identityToken: "once:2026-08-01T10:05:00Z");

        harness.StartLoop(RunnerHarness.Definition(b => b.OnceAt(T0.AddMinutes(5))));

        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 1);
    }

    [Fact]
    public async Task OverlapSkip_SkipsOccurrences_WhileThePreviousRunIsActive()
    {
        var release = new SemaphoreSlim(0);
        await using var harness = new LoopHarness(async (_, ct) => await release.WaitAsync(ct));
        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(5))
            .PreventOverlappingExecutions()));

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 1);

        // Two more occurrences pass while the first execution is still running — both skipped.
        harness.Time.Advance(TimeSpan.FromMinutes(10));
        await harness.AssertNotHappeningAsync(async () => await harness.CountAsync(JobId) > 1);

        release.Release();
        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 2);
        release.Release();

        Assert.Equal(2, await harness.CountAsync(JobId));
    }

    [Fact]
    public async Task OverlapQueue_QueuesAtMostOneExecution()
    {
        var release = new SemaphoreSlim(0);
        await using var harness = new LoopHarness(async (_, ct) => await release.WaitAsync(ct));
        harness.StartLoop(RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(5))
            .PreventOverlappingExecutions(OverlapPolicy.QueueSingleExecution)));

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 1);

        // Two occurrences pass while running: the first is queued, the second skipped.
        harness.Time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);

        release.Release(); // finish run 1 → the queued occurrence starts
        await harness.WaitUntilAsync(async () => await harness.CountAsync(JobId, "queued-overlap") >= 1);
        release.Release(); // finish the queued run

        await harness.WaitUntilAsync(async () =>
            (await harness.RecordsAsync(JobId)).Count(r => r.Status == JobExecutionStatus.Completed) >= 2);
        Assert.Equal(2, await harness.CountAsync(JobId)); // third occurrence was skipped, not queued
    }

    [Fact]
    public async Task ManualTrigger_RunsThroughTheNormalPipeline()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        var loop = harness.StartLoop(RunnerHarness.Definition()); // no schedule: manual-only job

        var request = new ManualTriggerRequest(
            "manual-exec-1", new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        loop.EnqueueManualTrigger(request);

        var executionId = await request.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("manual-exec-1", executionId);

        await harness.WaitUntilAsync(async () => await harness.CountAsync(JobId, "manual") >= 1);
        var record = (await harness.RecordsAsync(JobId)).Single();
        Assert.Equal("manual-exec-1", record.ExecutionId);
        Assert.StartsWith($"{JobId}:manual:", record.ScheduledExecutionId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailingJob_DoesNotStopTheLoop_NextOccurrenceStillRuns()
    {
        var calls = 0;
        await using var harness = new LoopHarness((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new PermanentJobException("always broken");
        });
        harness.StartLoop(RunnerHarness.Definition(b => b.WithInterval(TimeSpan.FromMinutes(5))));

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 2);

        Assert.True(calls >= 2, "the scheduler must keep scheduling after failures");
        Assert.All(await harness.RecordsAsync(JobId), r => Assert.Equal(JobExecutionStatus.Failed, r.Status));
    }

    [Fact]
    public async Task TwoLoops_AreIsolated_OneFailingJobDoesNotAffectTheOther()
    {
        var healthyRuns = 0;
        await using var harness = new LoopHarness((context, _) =>
        {
            if (context.JobId == "healthy-job")
            {
                Interlocked.Increment(ref healthyRuns);
                return Task.CompletedTask;
            }

            throw new TransientJobException("down");
        });

        harness.StartLoop(RunnerHarness.Definition(b => b.WithInterval(TimeSpan.FromMinutes(5)), jobId: "healthy-job"));
        harness.StartLoop(RunnerHarness.Definition(b => b.WithInterval(TimeSpan.FromMinutes(5)), jobId: "failing-job"));

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () =>
            await harness.CountAsync("healthy-job") >= 3 && await harness.CountAsync("failing-job") >= 3);

        Assert.True(healthyRuns >= 3);
        Assert.All(await harness.RecordsAsync("failing-job"), r => Assert.Equal(JobExecutionStatus.Failed, r.Status));
        Assert.All(await harness.RecordsAsync("healthy-job"), r => Assert.Equal(JobExecutionStatus.Completed, r.Status));
    }
}
