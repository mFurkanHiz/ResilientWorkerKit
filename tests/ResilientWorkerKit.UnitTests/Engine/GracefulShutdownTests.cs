using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

public class GracefulShutdownTests
{
    private const string JobId = "test-job";

    [Fact]
    public async Task RunningJob_ReceivesTheCancellationToken()
    {
        var tokenObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                // The job body sees the same token the engine was stopped with.
                tokenObserved.TrySetResult(context.CancellationToken.IsCancellationRequested);
                throw;
            }
        });

        using var cts = new CancellationTokenSource();
        var run = harness.RunAsync(RunnerHarness.Definition(), cts.Token);
        await started.Task;
        cts.Cancel();

        Assert.True(await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(JobExecutionStatus.Cancelled, (await run)!.Status);
    }

    [Fact]
    public async Task InterruptedExecution_IsCancelled_NeverCompleted()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        using var cts = new CancellationTokenSource();
        var run = harness.RunAsync(RunnerHarness.Definition(), cts.Token);
        await started.Task;
        cts.Cancel();
        await run;

        var record = await harness.Executions.GetLatestAsync(JobId);
        Assert.Equal(JobExecutionStatus.Cancelled, record!.Status);
        Assert.NotEqual(JobExecutionStatus.Completed, record.Status);
        Assert.NotNull(record.CompletedAtUtc);
    }

    [Fact]
    public async Task CheckpointSavedBeforeShutdown_Survives_AndIsNotAdvancedFurther()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            // Batch 1 succeeded and its checkpoint was persisted.
            await context.Checkpoints.SaveAsync(new TestCheckpoint("page-2", 1), ct);
            started.TrySetResult();
            // Batch 2 is interrupted by shutdown before it can advance the checkpoint.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            await context.Checkpoints.SaveAsync(new TestCheckpoint("page-3", 2), ct);
        });

        using var cts = new CancellationTokenSource();
        var run = harness.RunAsync(RunnerHarness.Definition(), cts.Token);
        await started.Task;
        cts.Cancel();
        await run;

        var stored = await harness.Checkpoints.GetAsync(JobId);
        Assert.Contains("page-2", stored!.PayloadJson);
        Assert.DoesNotContain("page-3", stored.PayloadJson);
    }

    [Fact]
    public async Task LockIsReleased_AfterCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = true;
        await using var harness = new RunnerHarness(async (_, ct) =>
        {
            if (!block)
            {
                return;
            }

            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        var definition = RunnerHarness.Definition(b => b.PreventOverlappingExecutions());
        using var cts = new CancellationTokenSource();
        var run = harness.RunAsync(definition, cts.Token);
        await started.Task;
        cts.Cancel();
        await run;

        // The lock the cancelled execution held must be free for the next execution.
        block = false;
        var next = await harness.RunAsync(definition);
        Assert.NotNull(next);
        Assert.Equal(JobExecutionStatus.Completed, next.Status);
    }

    [Fact]
    public async Task StartupRecovery_MarksStaleRunningExecutionsAsAbandoned()
    {
        await using var harness = new RunnerHarness((_, _) => Task.CompletedTask);
        await harness.Executions.CreateAsync(new JobExecutionRecord
        {
            JobId = JobId,
            ExecutionId = "crashed-execution",
            ScheduledExecutionId = $"{JobId}:x",
            ScheduledAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status = JobExecutionStatus.Running,
        });

        var recovered = await harness.Executions.MarkRunningAsAbandonedAsync();

        Assert.Equal(1, recovered);
        var record = await harness.Executions.GetAsync("crashed-execution");
        Assert.Equal(JobExecutionStatus.Abandoned, record!.Status);
        Assert.Equal(JobFailureKind.Abandoned, record.FailureKind);
    }

    [Fact]
    public async Task LoopStopsStartingNewOccurrences_AfterCancellation()
    {
        await using var harness = new LoopHarness((_, _) => Task.CompletedTask);
        harness.StartLoop(RunnerHarness.Definition(b => b.WithInterval(TimeSpan.FromMinutes(5))));

        await harness.AdvanceUntilAsync(TimeSpan.FromMinutes(1), async () => await harness.CountAsync(JobId) >= 1);
        await harness.StopAsync();

        var countAtStop = await harness.CountAsync(JobId);
        harness.Time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(100);

        Assert.Equal(countAtStop, await harness.CountAsync(JobId));
    }
}
