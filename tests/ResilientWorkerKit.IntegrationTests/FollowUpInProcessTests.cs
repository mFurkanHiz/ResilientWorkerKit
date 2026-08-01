using Microsoft.Extensions.DependencyInjection;
using ResilientWorkerKit.IntegrationTests.Infrastructure;

namespace ResilientWorkerKit.IntegrationTests;

/// <summary>
/// Follow-up retries inside a single running host, with a job body that awaits.
/// <para>
/// v1.1's follow-up tests all used a job that threw synchronously, over a SQLite provider whose
/// async API is synchronous — so an execution finished inside the call that started it and the
/// scheduler never had to notice the queued row. Any job that performs real I/O awaits, and for
/// those the queued follow-up was never picked up by its own host. These tests fix the shape of
/// the job rather than the shape of the assertion.
/// </para>
/// </summary>
public class FollowUpInProcessTests
{
    private const string JobId = "awaiting-job";

    private sealed class Attempts
    {
        private int _n;
        public int Value => Volatile.Read(ref _n);
        public void Bump() => Interlocked.Increment(ref _n);
    }

    /// <summary>Awaits before failing, the way a job calling an API or a database does.</summary>
    private sealed class AwaitingFailingJob : IWorkerJob
    {
        private readonly Attempts _attempts;

        public AwaitingFailingJob(Attempts attempts) => _attempts = attempts;

        public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            _attempts.Bump();
            await Task.Delay(20, cancellationToken);
            throw new TransientJobException("upstream is not answering");
        }
    }

    [Fact]
    public async Task FollowUpsRunInTheSameHost_AndDrainTheQueue()
    {
        using var database = new SqliteDatabase();
        var attempts = new Attempts();

        var host = await StartAsync(database, attempts, maxAttempts: 3);
        await using (host)
        {
            // original + 3 follow-ups, all inside one host, with no restart involved. Waiting on
            // the terminal status rather than the record count: a record exists from the moment
            // an execution starts, so counting records can see one that is still Running.
            await WorkerHost.WaitUntilAsync(
                async () => await host.CountAsync(JobId, JobExecutionStatus.Failed) >= 4);
            await WorkerHost.WaitUntilAsync(async () => await host.PendingOccurrences().CountAsync(JobId) == 0);

            Assert.Equal(4, attempts.Value);

            var history = await host.HistoryAsync(JobId);
            Assert.Equal(3, history.Count(r => r.TriggerType == "follow-up"));
            Assert.All(history, r => Assert.Equal(JobExecutionStatus.Failed, r.Status));
        }
    }

    [Fact]
    public async Task FollowUpIdentitiesStayBounded()
    {
        using var database = new SqliteDatabase();
        var attempts = new Attempts();

        var host = await StartAsync(database, attempts, maxAttempts: 3);
        await using (host)
        {
            await WorkerHost.WaitUntilAsync(
                async () => await host.CountAsync(JobId, JobExecutionStatus.Failed) >= 4);

            var identities = (await host.HistoryAsync(JobId)).Select(r => r.ScheduledExecutionId).ToList();

            Assert.All(identities, id =>
            {
                // One segment at most: derived from the origin, not chained onto the last retry.
                Assert.True(id.Split("followup-").Length - 1 <= 1, $"identity accumulated segments: {id}");
                // The persisted column is nvarchar(300) and SQL Server enforces it.
                Assert.True(id.Length <= 300, $"identity would overflow the column: {id.Length} chars");
            });

            Assert.Contains(identities, id => id.EndsWith("+followup-3", StringComparison.Ordinal));
        }
    }

    private static Task<WorkerHost> StartAsync(SqliteDatabase database, Attempts attempts, int maxAttempts)
        => WorkerHost.StartAsync(
            database,
            kit => kit.AddJob<AwaitingFailingJob>(JobId, job => job
                .AtTimes(DateTimeOffset.UtcNow.AddSeconds(-5))
                .PreventOverlappingExecutions()
                .WithRetryCount(0)
                .RetryLater(o =>
                {
                    o.MaxAttempts = maxAttempts;
                    o.Delay = TimeSpan.FromMilliseconds(150);
                    o.MaxDelay = TimeSpan.FromMilliseconds(150);
                })),
            services => services.AddSingleton(attempts));
}
