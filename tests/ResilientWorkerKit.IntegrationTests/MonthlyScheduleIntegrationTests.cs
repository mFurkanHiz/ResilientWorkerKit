using Microsoft.Extensions.DependencyInjection;
using ResilientWorkerKit.IntegrationTests.Infrastructure;
using ResilientWorkerKit.Scheduling;

namespace ResilientWorkerKit.IntegrationTests;

/// <summary>
/// Monthly-job behavior across host restarts, verified against the real durable store:
/// a completed monthly occurrence must never run a second time, while a *failed* one must
/// stay retryable and the following month must run normally.
/// </summary>
public class MonthlyScheduleIntegrationTests
{
    private const string JobId = "monthly-billing";

    [Fact]
    public async Task CompletedMonthlyOccurrence_IsNotRepeatedAfterRestart()
    {
        using var database = new SqliteDatabase();
        var counter = new SucceedingJobCounter();

        // The August occurrence already completed before the restart.
        await SeedOccurrenceAsync(database, "2026-08", JobExecutionStatus.Completed);

        // A schedule that is "due right now" for the same August identity.
        var host = await StartMonthlyHostAsync(database, counter, new FixedIdentitySchedule("2026-08"));
        await using (host)
        {
            await AssertStaysAsync(async () => await host.CountAsync(JobId) == 1);
            Assert.Equal(0, counter.Count); // the job body never ran again
        }
    }

    [Fact]
    public async Task FailedMonthlyOccurrence_IsRetriedByTheNextSchedulePass()
    {
        using var database = new SqliteDatabase();
        var counter = new SucceedingJobCounter();

        // August failed previously — a failed occurrence must remain eligible.
        await SeedOccurrenceAsync(database, "2026-08", JobExecutionStatus.Failed);

        var host = await StartMonthlyHostAsync(database, counter, new FixedIdentitySchedule("2026-08"));
        await using (host)
        {
            // Wait for the recorded outcome, not for the job body: the counter is incremented
            // inside the execution, while the Completed record is written after it returns.
            await WorkerHost.WaitUntilAsync(async () => (await host.HistoryAsync(JobId))
                .Any(r => r.ScheduledExecutionId == $"{JobId}:2026-08"
                          && r.Status == JobExecutionStatus.Completed));

            var august = (await host.HistoryAsync(JobId))
                .Where(r => r.ScheduledExecutionId == $"{JobId}:2026-08")
                .ToList();
            Assert.Contains(august, r => r.Status == JobExecutionStatus.Failed);
            Assert.Contains(august, r => r.Status == JobExecutionStatus.Completed);
            Assert.True(counter.Count >= 1, "the job body must have executed");
        }
    }

    [Fact]
    public async Task NextMonth_RunsAgain_WithItsOwnIdentity()
    {
        using var database = new SqliteDatabase();
        var counter = new SucceedingJobCounter();
        await SeedOccurrenceAsync(database, "2026-08", JobExecutionStatus.Completed);

        // September is a different identity, so it is not suppressed by August's record.
        var host = await StartMonthlyHostAsync(database, counter, new FixedIdentitySchedule("2026-09"));
        await using (host)
        {
            // Wait for the September identity specifically — the seeded August record is
            // already Completed, so a plain "any completed execution" check would pass instantly.
            await WorkerHost.WaitUntilAsync(async () => (await host.HistoryAsync(JobId))
                .Any(r => r.ScheduledExecutionId == $"{JobId}:2026-09" && r.Status == JobExecutionStatus.Completed));

            var identities = (await host.HistoryAsync(JobId)).Select(r => r.ScheduledExecutionId).ToHashSet();
            Assert.Contains($"{JobId}:2026-08", identities);
            Assert.Contains($"{JobId}:2026-09", identities);
        }
    }

    [Fact]
    public async Task FailingMonthlyJob_DoesNotStopTheHostOrOtherJobs()
    {
        using var database = new SqliteDatabase();
        var otherCounter = new SucceedingJobCounter();

        var host = await WorkerHost.StartAsync(
            database,
            kit =>
            {
                kit.AddJob<AlwaysFailingJob>(JobId, job => job
                    .WithSchedule(new FixedIdentitySchedule("2026-08"))
                    .RunOnStartup()
                    .WithRetry(r =>
                    {
                        r.MaxRetries = 2;
                        r.BaseDelay = TimeSpan.FromMilliseconds(10);
                        r.MaxDelay = TimeSpan.FromMilliseconds(20);
                        r.JitterFactor = 0;
                    }));

                kit.AddJob<SucceedingJob>("other-job", job => job
                    .WithInterval(TimeSpan.FromSeconds(1))
                    .RunOnStartup());
            },
            services => services.AddSingleton(otherCounter));

        await using (host)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host.CountAsync(JobId, JobExecutionStatus.Failed) >= 1 &&
                await host.CountAsync("other-job", JobExecutionStatus.Completed) >= 2);

            var failure = (await host.HistoryAsync(JobId)).First(r => r.Status == JobExecutionStatus.Failed);
            Assert.Equal(3, failure.AttemptCount);
            Assert.True(otherCounter.Count >= 2, "the unrelated job must keep running on schedule");
        }
    }

    private static async Task SeedOccurrenceAsync(SqliteDatabase database, string identityToken, JobExecutionStatus status)
    {
        var host = await WorkerHost.StartAsync(
            database,
            kit => kit.AddJob<SucceedingJob>(JobId, job => job.Disabled()),
            services => services.AddSingleton(new SucceedingJobCounter()));

        await using (host)
        {
            var now = DateTimeOffset.UtcNow.AddDays(-1);
            await host.Executions().CreateAsync(new JobExecutionRecord
            {
                JobId = JobId,
                ExecutionId = Guid.NewGuid().ToString("n"),
                ScheduledExecutionId = $"{JobId}:{identityToken}",
                ScheduledAtUtc = now,
                StartedAtUtc = now,
                CompletedAtUtc = now.AddMinutes(1),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Status = status,
                TriggerType = "schedule",
            });
        }
    }

    private static Task<WorkerHost> StartMonthlyHostAsync(
        SqliteDatabase database, SucceedingJobCounter counter, IJobSchedule schedule)
        => WorkerHost.StartAsync(
            database,
            kit => kit.AddJob<SucceedingJob>(JobId, job => job
                .WithSchedule(schedule)
                .PreventOverlappingExecutions()),
            services => services.AddSingleton(counter));

    private static async Task AssertStaysAsync(Func<Task<bool>> condition, int iterations = 30)
    {
        for (var i = 0; i < iterations; i++)
        {
            Assert.True(await condition(), "the condition stopped holding");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// A schedule that is immediately due and always reports the same occurrence identity —
    /// exactly the shape of a monthly schedule whose month is current, which is what makes
    /// the identity-based duplicate suppression observable in a fast test.
    /// </summary>
    private sealed class FixedIdentitySchedule : IJobSchedule
    {
        private readonly string _identityToken;

        public FixedIdentitySchedule(string identityToken) => _identityToken = identityToken;

        public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
            => new(afterUtc.AddMilliseconds(50), afterUtc.UtcDateTime, _identityToken);

        public string Describe() => $"fixed monthly identity {_identityToken}";
    }

    private sealed class AlwaysFailingJob : IWorkerJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
            => throw new TransientJobException("monthly billing upstream is down");
    }
}
