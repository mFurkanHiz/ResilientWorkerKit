using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

public class JobRunnerExecutionTests
{
    [Fact]
    public async Task SuccessfulExecution_IsRecordedCompleted()
    {
        await using var harness = new RunnerHarness((_, _) => Task.CompletedTask);

        var result = await harness.RunAsync(RunnerHarness.Definition());

        Assert.NotNull(result);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);

        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.NotNull(record);
        Assert.Equal(JobExecutionStatus.Completed, record.Status);
        Assert.Equal(1, record.AttemptCount);
        Assert.NotNull(record.DurationMs);
        Assert.NotNull(record.CompletedAtUtc);
    }

    [Fact]
    public async Task PermanentFailure_FailsWithoutRetry()
    {
        var attempts = 0;
        await using var harness = new RunnerHarness((_, _) =>
        {
            attempts++;
            throw new PermanentJobException("validation failed");
        });

        var result = await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetryCount(5)));

        Assert.Equal(JobExecutionStatus.Failed, result!.Status);
        Assert.Equal(1, attempts);

        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(JobFailureKind.Permanent, record!.FailureKind);
        Assert.Equal(typeof(PermanentJobException).FullName, record.ErrorType);
        Assert.Equal("validation failed", record.ErrorMessage);
        Assert.Contains("PermanentJobException", record.ErrorDetail);
    }

    [Fact]
    public async Task MisconfiguredFailure_IsNeverRetried()
    {
        var attempts = 0;
        await using var harness = new RunnerHarness((_, _) =>
        {
            attempts++;
            throw new JobConfigurationException("broken checkpoint");
        });

        var result = await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetryCount(5)));

        Assert.Equal(JobExecutionStatus.Failed, result!.Status);
        Assert.Equal(1, attempts);
        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(JobFailureKind.Misconfigured, record!.FailureKind);
    }

    [Fact]
    public async Task Cancellation_ProducesCancelledStatus()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        using var cts = new CancellationTokenSource();
        var runTask = harness.RunAsync(RunnerHarness.Definition(), cts.Token);
        await started.Task;
        cts.Cancel();

        var result = await runTask;
        Assert.Equal(JobExecutionStatus.Cancelled, result!.Status);

        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(JobExecutionStatus.Cancelled, record!.Status);
        Assert.Equal(JobFailureKind.Cancelled, record.FailureKind);
    }

    [Fact]
    public async Task TotalTimeout_ProducesTimedOutStatus()
    {
        await using var harness = new RunnerHarness((_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var result = await harness.RunAsync(RunnerHarness.Definition(b => b.WithTimeout(TimeSpan.FromMilliseconds(100))));

        Assert.Equal(JobExecutionStatus.TimedOut, result!.Status);
        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(JobFailureKind.TimedOut, record!.FailureKind);
    }

    [Fact]
    public async Task ExecutionDuration_IsMeasured()
    {
        await using var harness = new RunnerHarness((_, ct) => Task.Delay(50, ct));

        await harness.RunAsync(RunnerHarness.Definition());

        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.True(record!.DurationMs >= 30, $"expected ≥30 ms, got {record.DurationMs}");
    }

    [Fact]
    public async Task RunnerNeverThrows_EvenWhenTheExecutionStoreIsBroken()
    {
        await using var harness = new RunnerHarness((_, _) => Task.CompletedTask,
            executionStore: new ThrowingExecutionStore());

        // The store throws on every call; the execution must still run and report success.
        var result = await harness.RunAsync(RunnerHarness.Definition());

        Assert.Equal(JobExecutionStatus.Completed, result!.Status);
    }

    [Fact]
    public async Task OverlapLock_SecondConcurrentRunIsSkipped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });

        var definition = RunnerHarness.Definition(); // SkipNewExecution by default
        var first = harness.RunAsync(definition);
        await started.Task;
        var second = await harness.RunAsync(definition);

        Assert.Null(second); // lock unavailable → skipped
        release.TrySetResult();
        Assert.Equal(JobExecutionStatus.Completed, (await first)!.Status);
    }

    private sealed class ThrowingExecutionStore : IJobExecutionStore
    {
        public Task CreateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task UpdateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<JobExecutionRecord?> GetAsync(string executionId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<JobExecutionRecord?> GetLatestAsync(string jobId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<IReadOnlyList<JobExecutionRecord>> GetRecentAsync(string jobId, int count, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<bool> ExistsForScheduledExecutionAsync(string jobId, string scheduledExecutionId, bool completedOnly, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<int> MarkRunningAsAbandonedAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");
    }
}
