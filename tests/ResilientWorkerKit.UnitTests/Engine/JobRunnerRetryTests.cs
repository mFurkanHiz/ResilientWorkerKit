using System.Diagnostics;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

public class JobRunnerRetryTests
{
    [Fact]
    public async Task TransientFailure_IsRetried_UntilSuccess()
    {
        var executionIds = new List<string>();
        var attemptNumbers = new List<int>();
        await using var harness = new RunnerHarness((context, _) =>
        {
            executionIds.Add(context.ExecutionId);
            attemptNumbers.Add(context.AttemptNumber);
            if (context.AttemptNumber < 3)
            {
                throw new TransientJobException("blip");
            }

            return Task.CompletedTask;
        });

        var result = await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetryCount(3)));

        Assert.Equal(JobExecutionStatus.Completed, result!.Status);
        Assert.Equal([1, 2, 3], attemptNumbers);
        Assert.Single(executionIds.Distinct()); // ExecutionId is stable across attempts

        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(3, record!.AttemptCount);
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        var attempts = 0;
        await using var harness = new RunnerHarness((_, _) =>
        {
            attempts++;
            throw new PermanentJobException("no");
        });

        await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetryCount(4)));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetriesExhausted_ExecutionFails_WithTransientKind()
    {
        var attempts = 0;
        await using var harness = new RunnerHarness((_, _) =>
        {
            attempts++;
            throw new TransientJobException("always down");
        });

        var result = await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetryCount(2)));

        Assert.Equal(JobExecutionStatus.Failed, result!.Status);
        Assert.Equal(3, attempts); // 1 initial + 2 retries

        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(JobFailureKind.Transient, record!.FailureKind);
        Assert.Equal(3, record.AttemptCount);
    }

    [Fact]
    public async Task ExhaustedRetries_WriteExecutionDeadLetter_WhenConfigured()
    {
        await using var harness = new RunnerHarness((_, _) => throw new TransientJobException("down"));

        await harness.RunAsync(RunnerHarness.Definition(b => b
            .WithRetryCount(1)
            .DeadLetterOnExhaustedRetries()));

        var deadLetters = await harness.DeadLetters.GetPendingAsync("test-job", 10);
        var record = Assert.Single(deadLetters);
        Assert.Equal("execution", record.Scope);
        Assert.Equal(2, record.AttemptCount);
        Assert.Contains("down", record.Reason);
    }

    [Fact]
    public async Task NoDeadLetter_WhenNotConfigured()
    {
        await using var harness = new RunnerHarness((_, _) => throw new TransientJobException("down"));

        await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetryCount(1)));

        Assert.Empty(await harness.DeadLetters.GetPendingAsync("test-job", 10));
    }

    [Fact]
    public async Task RetryAfterHint_DelaysTheNextAttempt()
    {
        var attempts = 0;
        await using var harness = new RunnerHarness((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new TransientJobException("throttled", retryAfter: TimeSpan.FromMilliseconds(250));
            }

            return Task.CompletedTask;
        });

        var stopwatch = Stopwatch.StartNew();
        var result = await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetryCount(1)));
        stopwatch.Stop();

        Assert.Equal(JobExecutionStatus.Completed, result!.Status);
        Assert.True(stopwatch.ElapsedMilliseconds >= 200,
            $"Retry-After of 250 ms should delay the retry; elapsed {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AttemptTimeout_RetriesTheAttempt_ThenFails()
    {
        var attempts = 0;
        await using var harness = new RunnerHarness(async (_, ct) =>
        {
            attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        var result = await harness.RunAsync(RunnerHarness.Definition(b => b.WithRetry(r =>
        {
            r.MaxRetries = 1;
            r.AttemptTimeout = TimeSpan.FromMilliseconds(80);
            r.BaseDelay = TimeSpan.Zero;
            r.MaxDelay = TimeSpan.Zero;
            r.JitterFactor = 0;
        })));

        Assert.Equal(2, attempts); // both attempts timed out
        Assert.Equal(JobExecutionStatus.Failed, result!.Status);
        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(JobFailureKind.Transient, record!.FailureKind);
    }

    [Fact]
    public async Task CancellationDuringRetryDelay_ProducesCancelled()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new RunnerHarness((_, _) =>
        {
            failed.TrySetResult();
            throw new TransientJobException("down");
        });

        using var cts = new CancellationTokenSource();
        var runTask = harness.RunAsync(RunnerHarness.Definition(b => b.WithRetry(r =>
        {
            r.MaxRetries = 3;
            r.BaseDelay = TimeSpan.FromSeconds(30); // the cancel lands inside this delay
            r.MaxDelay = TimeSpan.FromSeconds(30);
            r.JitterFactor = 0;
        })), cts.Token);

        await failed.Task;
        cts.Cancel();

        var result = await runTask;
        Assert.Equal(JobExecutionStatus.Cancelled, result!.Status);
    }
}
