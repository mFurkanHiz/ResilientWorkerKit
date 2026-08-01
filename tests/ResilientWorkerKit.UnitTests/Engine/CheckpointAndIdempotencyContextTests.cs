using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

public sealed record TestCheckpoint(string? Token, int Page);

public class CheckpointAndIdempotencyContextTests
{
    [Fact]
    public async Task Checkpoint_RoundTrips_TypedState()
    {
        TestCheckpoint? firstRead = null;
        TestCheckpoint? secondRead = null;
        var run = 0;
        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            run++;
            if (run == 1)
            {
                firstRead = await context.Checkpoints.GetAsync<TestCheckpoint>(ct);
                await context.Checkpoints.SaveAsync(new TestCheckpoint("page-2", 1), ct);
            }
            else
            {
                secondRead = await context.Checkpoints.GetAsync<TestCheckpoint>(ct);
            }
        });

        var definition = RunnerHarness.Definition();
        await harness.RunAsync(definition);
        await harness.RunAsync(definition);

        Assert.Null(firstRead);
        Assert.Equal(new TestCheckpoint("page-2", 1), secondRead);
    }

    [Fact]
    public async Task Checkpoint_SummaryLandsInExecutionRecordAndHealth()
    {
        await using var harness = new RunnerHarness(async (context, ct) =>
            await context.Checkpoints.SaveAsync(new TestCheckpoint("t", 3), ct));

        await harness.RunAsync(RunnerHarness.Definition());

        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Contains("TestCheckpoint", record!.LastCheckpointSummary);
        Assert.Contains("TestCheckpoint", harness.Health.Get("test-job")!.LastCheckpointSummary);
    }

    [Fact]
    public async Task FailedBatch_DoesNotAdvanceTheCheckpoint()
    {
        var run = 0;
        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            run++;
            if (run == 1)
            {
                await context.Checkpoints.SaveAsync(new TestCheckpoint("page-2", 1), ct);
                // Batch 2 fails AFTER batch 1's checkpoint was saved:
                throw new PermanentJobException("batch 2 failed");
            }
        });

        await harness.RunAsync(RunnerHarness.Definition());

        var stored = await harness.Checkpoints.GetAsync("test-job");
        Assert.NotNull(stored);
        Assert.Contains("page-2", stored.PayloadJson); // exactly the last successful batch — nothing further
    }

    [Fact]
    public async Task CorruptedCheckpoint_FailsAsMisconfigured_NotSilently()
    {
        await using var harness = new RunnerHarness(async (context, ct) =>
            await context.Checkpoints.GetAsync<TestCheckpoint>(ct));

        await harness.Checkpoints.SaveAsync(
            new JobCheckpoint("test-job", "{not valid json", null, DateTimeOffset.UtcNow));

        var result = await harness.RunAsync(RunnerHarness.Definition());

        Assert.Equal(JobExecutionStatus.Failed, result!.Status);
        var record = await harness.Executions.GetLatestAsync("test-job");
        Assert.Equal(JobFailureKind.Misconfigured, record!.FailureKind);
        Assert.Contains("checkpoint", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checkpoint_CanBeCleared()
    {
        var run = 0;
        TestCheckpoint? afterClear = null;
        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            run++;
            if (run == 1)
            {
                await context.Checkpoints.SaveAsync(new TestCheckpoint("x", 1), ct);
                await context.Checkpoints.ClearAsync(ct);
            }
            else
            {
                afterClear = await context.Checkpoints.GetAsync<TestCheckpoint>(ct);
            }
        });

        var definition = RunnerHarness.Definition();
        await harness.RunAsync(definition);
        await harness.RunAsync(definition);

        Assert.Null(afterClear);
    }

    [Fact]
    public async Task Idempotency_SameKey_IsNotProcessedTwice()
    {
        var sideEffects = 0;
        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            if (await context.Idempotency.TryAcquireAsync("item:1:v1", ct) == IdempotencyAcquireResult.Acquired)
            {
                sideEffects++;
                await context.Idempotency.MarkCompletedAsync("item:1:v1", ct);
            }
        });

        var definition = RunnerHarness.Definition();
        await harness.RunAsync(definition);
        await harness.RunAsync(definition);

        Assert.Equal(1, sideEffects);
    }

    [Fact]
    public async Task Idempotency_FailedKey_CanBeRetriedLater()
    {
        var acquisitions = new List<IdempotencyAcquireResult>();
        var run = 0;
        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            run++;
            var result = await context.Idempotency.TryAcquireAsync("item:2:v1", ct);
            acquisitions.Add(result);
            if (run == 1)
            {
                await context.Idempotency.MarkFailedAsync("item:2:v1", ct);
            }
            else
            {
                await context.Idempotency.MarkCompletedAsync("item:2:v1", ct);
            }
        });

        var definition = RunnerHarness.Definition();
        await harness.RunAsync(definition);
        await harness.RunAsync(definition);

        Assert.Equal([IdempotencyAcquireResult.Acquired, IdempotencyAcquireResult.Acquired], acquisitions);
    }

    [Fact]
    public async Task Idempotency_ExistsReflectsCompletedKeys()
    {
        var exists = new List<bool>();
        var run = 0;
        await using var harness = new RunnerHarness(async (context, ct) =>
        {
            run++;
            exists.Add(await context.Idempotency.ExistsAsync("item:3:v1", ct));
            if (run == 1)
            {
                await context.Idempotency.TryAcquireAsync("item:3:v1", ct);
                await context.Idempotency.MarkCompletedAsync("item:3:v1", ct);
            }
        });

        var definition = RunnerHarness.Definition();
        await harness.RunAsync(definition);
        await harness.RunAsync(definition);

        Assert.Equal([false, true], exists);
    }
}
