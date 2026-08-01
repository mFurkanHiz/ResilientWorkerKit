using Microsoft.Extensions.Time.Testing;
using ResilientWorkerKit.Stores;

namespace ResilientWorkerKit.UnitTests.Stores;

public class InMemoryIdempotencyStoreTests
{
    [Fact]
    public async Task ConcurrentAcquires_ExactlyOneWinner()
    {
        var store = new InMemoryIdempotencyStore();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
            Task.Run(() => store.TryAcquireAsync("job", "key", $"exec-{i}", null))));

        Assert.Equal(1, results.Count(r => r == IdempotencyAcquireResult.Acquired));
        Assert.Equal(31, results.Count(r => r == IdempotencyAcquireResult.InProgressElsewhere));
    }

    [Fact]
    public async Task CompletedRecord_YieldsAlreadyCompleted()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryAcquireAsync("job", "key", "exec-1", null);
        await store.MarkCompletedAsync("job", "key");

        var result = await store.TryAcquireAsync("job", "key", "exec-2", null);

        Assert.Equal(IdempotencyAcquireResult.AlreadyCompleted, result);
        Assert.True(await store.ExistsCompletedAsync("job", "key"));
    }

    [Fact]
    public async Task FailedRecord_CanBeReacquired()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryAcquireAsync("job", "key", "exec-1", null);
        await store.MarkFailedAsync("job", "key");

        var result = await store.TryAcquireAsync("job", "key", "exec-2", null);

        Assert.Equal(IdempotencyAcquireResult.Acquired, result);
        var record = await store.GetAsync("job", "key");
        Assert.Equal("exec-2", record!.ExecutionId);
    }

    [Fact]
    public async Task ExpiredRecord_IsReusable()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T10:00:00Z"));
        var store = new InMemoryIdempotencyStore(time);
        await store.TryAcquireAsync("job", "key", "exec-1", time.GetUtcNow().AddMinutes(5));
        await store.MarkCompletedAsync("job", "key");

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.False(await store.ExistsCompletedAsync("job", "key"));
        Assert.Equal(IdempotencyAcquireResult.Acquired,
            await store.TryAcquireAsync("job", "key", "exec-2", null));
    }

    [Fact]
    public async Task SameExecution_ReacquiringItsOwnPendingKey_IsAcquired()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryAcquireAsync("job", "key", "exec-1", null);

        // A retry attempt of the same execution sees its own pending record.
        Assert.Equal(IdempotencyAcquireResult.Acquired,
            await store.TryAcquireAsync("job", "key", "exec-1", null));
    }

    [Fact]
    public async Task Remove_DeletesTheRecord()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryAcquireAsync("job", "key", "exec-1", null);
        await store.RemoveAsync("job", "key");

        Assert.Null(await store.GetAsync("job", "key"));
    }
}

public class InProcessJobLockProviderTests
{
    [Fact]
    public async Task SecondAcquire_FailsUntilReleased()
    {
        var provider = new InProcessJobLockProvider();

        var first = await provider.TryAcquireAsync("job", TimeSpan.Zero);
        var second = await provider.TryAcquireAsync("job", TimeSpan.Zero);

        Assert.NotNull(first);
        Assert.Null(second);

        await first.DisposeAsync();
        var third = await provider.TryAcquireAsync("job", TimeSpan.Zero);
        Assert.NotNull(third);
        await third.DisposeAsync();
    }

    [Fact]
    public async Task DifferentJobs_LockIndependently()
    {
        var provider = new InProcessJobLockProvider();

        var a = await provider.TryAcquireAsync("job-a", TimeSpan.Zero);
        var b = await provider.TryAcquireAsync("job-b", TimeSpan.Zero);

        Assert.NotNull(a);
        Assert.NotNull(b);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task LockIsReleased_WhenTheHolderThrows()
    {
        var provider = new InProcessJobLockProvider();

        try
        {
            var handle = await provider.TryAcquireAsync("job", TimeSpan.Zero);
            await using (handle)
            {
                throw new InvalidOperationException("boom");
            }
        }
        catch (InvalidOperationException)
        {
        }

        Assert.NotNull(await provider.TryAcquireAsync("job", TimeSpan.Zero));
    }

    [Fact]
    public async Task DoubleDispose_ReleasesOnlyOnce()
    {
        var provider = new InProcessJobLockProvider();
        var handle = await provider.TryAcquireAsync("job", TimeSpan.Zero);

        await handle!.DisposeAsync();
        await handle.DisposeAsync(); // must not over-release the semaphore

        var next = await provider.TryAcquireAsync("job", TimeSpan.Zero);
        Assert.NotNull(next);
        Assert.Null(await provider.TryAcquireAsync("job", TimeSpan.Zero));
        await next.DisposeAsync();
    }
}

public class InMemoryExecutionStoreTests
{
    private static JobExecutionRecord Record(string executionId, string jobId = "job", JobExecutionStatus status = JobExecutionStatus.Running, string scheduledExecutionId = "job:x", DateTimeOffset? startedAt = null) => new()
    {
        JobId = jobId,
        ExecutionId = executionId,
        ScheduledExecutionId = scheduledExecutionId,
        ScheduledAtUtc = startedAt ?? DateTimeOffset.UtcNow,
        StartedAtUtc = startedAt ?? DateTimeOffset.UtcNow,
        CreatedAtUtc = startedAt ?? DateTimeOffset.UtcNow,
        Status = status,
    };

    [Fact]
    public async Task MarkRunningAsAbandoned_OnlyAffectsRunningRecords()
    {
        var store = new InMemoryJobExecutionStore();
        await store.CreateAsync(Record("e1", status: JobExecutionStatus.Running));
        await store.CreateAsync(Record("e2", status: JobExecutionStatus.Completed));

        var count = await store.MarkRunningAsAbandonedAsync();

        Assert.Equal(1, count);
        Assert.Equal(JobExecutionStatus.Abandoned, (await store.GetAsync("e1"))!.Status);
        Assert.Equal(JobExecutionStatus.Completed, (await store.GetAsync("e2"))!.Status);
    }

    [Fact]
    public async Task ExistsForScheduledExecution_HonorsCompletedOnly()
    {
        var store = new InMemoryJobExecutionStore();
        await store.CreateAsync(Record("e1", status: JobExecutionStatus.Failed, scheduledExecutionId: "job:2026-08"));

        Assert.True(await store.ExistsForScheduledExecutionAsync("job", "job:2026-08", completedOnly: false));
        Assert.False(await store.ExistsForScheduledExecutionAsync("job", "job:2026-08", completedOnly: true));
    }

    [Fact]
    public async Task GetRecent_ReturnsNewestFirst()
    {
        var store = new InMemoryJobExecutionStore();
        var t0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        await store.CreateAsync(Record("old", startedAt: t0));
        await store.CreateAsync(Record("new", startedAt: t0.AddMinutes(5)));

        var recent = await store.GetRecentAsync("job", 10);

        Assert.Equal(["new", "old"], recent.Select(r => r.ExecutionId));
        Assert.Equal("new", (await store.GetLatestAsync("job"))!.ExecutionId);
    }
}
