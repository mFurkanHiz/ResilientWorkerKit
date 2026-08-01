using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ResilientWorkerKit.HealthChecks;
using ResilientWorkerKit.IntegrationTests.Infrastructure;

namespace ResilientWorkerKit.IntegrationTests;

/// <summary>Durable-store behavior against a real SQLite database.</summary>
public class EfCorePersistenceTests
{
    private const string JobId = "store-job";

    [Fact]
    public async Task ConcurrentIdempotencyAcquires_ExactlyOneWinner_SettledByTheDatabase()
    {
        using var database = new SqliteDatabase();
        var host = await StartAsync(database);
        await using (host)
        {
            var store = host.Idempotency();

            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
                Task.Run(() => store.TryAcquireAsync(JobId, "shared-key", $"exec-{i}", null))));

            Assert.Equal(1, results.Count(r => r == IdempotencyAcquireResult.Acquired));
            Assert.Equal(15, results.Count(r => r == IdempotencyAcquireResult.InProgressElsewhere));
        }
    }

    [Fact]
    public async Task IdempotencyLifecycle_CompletedThenExpired()
    {
        using var database = new SqliteDatabase();
        var host = await StartAsync(database);
        await using (host)
        {
            var store = host.Idempotency();

            Assert.Equal(IdempotencyAcquireResult.Acquired,
                await store.TryAcquireAsync(JobId, "k1", "exec-1", null));
            await store.MarkCompletedAsync(JobId, "k1");

            Assert.True(await store.ExistsCompletedAsync(JobId, "k1"));
            Assert.Equal(IdempotencyAcquireResult.AlreadyCompleted,
                await store.TryAcquireAsync(JobId, "k1", "exec-2", null));

            // An already-expired record behaves as if absent and can be re-acquired.
            await store.TryAcquireAsync(JobId, "k2", "exec-1", DateTimeOffset.UtcNow.AddMilliseconds(-1));
            await store.MarkCompletedAsync(JobId, "k2");
            Assert.False(await store.ExistsCompletedAsync(JobId, "k2"));
            Assert.Equal(IdempotencyAcquireResult.Acquired,
                await store.TryAcquireAsync(JobId, "k2", "exec-3", null));

            var record = await store.GetAsync(JobId, "k1");
            Assert.Equal(IdempotencyStatus.Completed, record!.Status);
            Assert.NotNull(record.CompletedAtUtc);
        }
    }

    [Fact]
    public async Task CheckpointAndDeadLetters_SurviveRestart()
    {
        using var database = new SqliteDatabase();
        var savedAt = DateTimeOffset.UtcNow;

        var host1 = await StartAsync(database);
        await using (host1)
        {
            await host1.Checkpoints().SaveAsync(
                new JobCheckpoint(JobId, """{"token":"page-7"}""", "TestState", savedAt));
            await host1.DeadLetters().AddAsync(new DeadLetterRecord
            {
                Id = "dl-1",
                JobId = JobId,
                ExecutionId = "exec-1",
                Scope = "item",
                ItemId = "item:42",
                Reason = "invalid payload",
                AttemptCount = 3,
                CreatedAtUtc = savedAt,
            });
        }

        var host2 = await StartAsync(database);
        await using (host2)
        {
            var checkpoint = await host2.Checkpoints().GetAsync(JobId);
            Assert.Equal("""{"token":"page-7"}""", checkpoint!.PayloadJson);
            Assert.Equal("TestState", checkpoint.PayloadType);
            Assert.Equal(savedAt.UtcDateTime, checkpoint.UpdatedAtUtc.UtcDateTime, TimeSpan.FromSeconds(1));

            var deadLetters = await host2.DeadLetters().GetPendingAsync(JobId, 10);
            var record = Assert.Single(deadLetters);
            Assert.Equal("item:42", record.ItemId);

            await host2.DeadLetters().MarkReprocessedAsync("dl-1");
            Assert.Empty(await host2.DeadLetters().GetPendingAsync(JobId, 10));
        }
    }

    [Fact]
    public async Task ExecutionHistory_IsQueryableAndOrdered()
    {
        using var database = new SqliteDatabase();
        var host = await StartAsync(database);
        await using (host)
        {
            var store = host.Executions();
            var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);

            for (var i = 0; i < 3; i++)
            {
                await store.CreateAsync(new JobExecutionRecord
                {
                    JobId = JobId,
                    ExecutionId = $"exec-{i}",
                    ScheduledExecutionId = $"{JobId}:{i}",
                    ScheduledAtUtc = baseTime.AddMinutes(i),
                    StartedAtUtc = baseTime.AddMinutes(i),
                    CreatedAtUtc = baseTime.AddMinutes(i),
                    UpdatedAtUtc = baseTime.AddMinutes(i),
                    Status = JobExecutionStatus.Completed,
                });
            }

            var recent = await store.GetRecentAsync(JobId, 10);
            Assert.Equal(["exec-2", "exec-1", "exec-0"], recent.Select(r => r.ExecutionId));
            Assert.Equal("exec-2", (await store.GetLatestAsync(JobId))!.ExecutionId);
            Assert.True(await store.ExistsForScheduledExecutionAsync(JobId, $"{JobId}:1", completedOnly: true));
            Assert.False(await store.ExistsForScheduledExecutionAsync(JobId, $"{JobId}:9", completedOnly: false));
        }
    }

    [Fact]
    public async Task HealthCheck_ReportsHealthy_ThroughTheRealDiPipeline()
    {
        using var database = new SqliteDatabase();
        var counter = new SucceedingJobCounter();

        var host = await WorkerHost.StartAsync(
            database,
            kit => kit.AddJob<SucceedingJob>(JobId, job => job
                .WithInterval(TimeSpan.FromSeconds(1))
                .RunOnStartup()),
            services =>
            {
                services.AddSingleton(counter);
                services.AddHealthChecks().AddResilientWorkerKit();
            });

        await using (host)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host.CountAsync(JobId, JobExecutionStatus.Completed) >= 1);

            var healthService = host.GetRequiredService<HealthCheckService>();
            var report = await healthService.CheckHealthAsync();

            Assert.Equal(HealthStatus.Healthy, report.Status);
            var entry = report.Entries["resilient-worker-kit"];
            Assert.Contains(JobId, entry.Data.Keys);
        }
    }

    private static Task<WorkerHost> StartAsync(SqliteDatabase database)
        => WorkerHost.StartAsync(
            database,
            kit => kit.AddJob<SucceedingJob>(JobId, job => job.Disabled()),
            services => services.AddSingleton(new SucceedingJobCounter()));
}
