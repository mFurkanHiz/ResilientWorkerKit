using Microsoft.Extensions.DependencyInjection;
using ResilientWorkerKit.IntegrationTests.Infrastructure;

namespace ResilientWorkerKit.IntegrationTests;

/// <summary>
/// The point of a durable follow-up retry: a planned one-off action still happens even if the
/// process is restarted while it is waiting to be retried. That is exactly the case the
/// in-execution retry cannot cover, because its wait lives in memory.
/// </summary>
public class FollowUpRetryTests
{
    private const string JobId = "sale-opening";

    /// <summary>Fails until a flag is flipped, then succeeds. Counts its executions.</summary>
    private sealed class FlippableJob : IWorkerJob
    {
        private readonly FlippableState _state;

        public FlippableJob(FlippableState state) => _state = state;

        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            _state.RecordAttempt(context.ScheduledExecutionId);
            if (_state.ShouldFail)
            {
                throw new TransientJobException("the upstream sale API is not answering yet");
            }

            _state.Succeeded = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FlippableState
    {
        private readonly List<string> _attempts = new();

        public volatile bool ShouldFail = true;

        public volatile bool Succeeded;

        public IReadOnlyList<string> Attempts
        {
            get { lock (_attempts) { return _attempts.ToList(); } }
        }

        public void RecordAttempt(string scheduledExecutionId)
        {
            lock (_attempts) { _attempts.Add(scheduledExecutionId); }
        }
    }

    [Fact]
    public async Task PlannedAction_ThatFails_IsRetried_AfterAFullHostRestart()
    {
        using var database = new SqliteDatabase();
        var saleTime = DateTimeOffset.UtcNow.AddSeconds(-5); // already due, so it fires at once

        // ---- Host #1: the planned action fires, fails, and queues a durable follow-up --------
        var firstState = new FlippableState { ShouldFail = true };
        var host1 = await StartAsync(database, firstState, saleTime);
        await using (host1)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host1.CountAsync(JobId, JobExecutionStatus.Failed) >= 1);

            await WorkerHost.WaitUntilAsync(async () =>
                await host1.PendingOccurrences().CountAsync(JobId) >= 1);

            var pending = await host1.PendingOccurrences().GetNextAsync(JobId);
            Assert.NotNull(pending);
            Assert.Equal(1, pending.FollowUpOrdinal);
            Assert.Equal(PendingOccurrenceSources.FollowUpRetry, pending.Source);
            Assert.Contains("followup-1", pending.IdentityToken);
        }

        // ---- Host #2: a brand new process, same database, upstream now healthy ---------------
        // Nothing about the follow-up lived in host #1's memory, so this is the real test.
        var secondState = new FlippableState { ShouldFail = false };
        var host2 = await StartAsync(database, secondState, saleTime);
        await using (host2)
        {
            // Wait for the recorded outcome, not the in-job flag: the execution record is
            // written after the job body returns.
            await WorkerHost.WaitUntilAsync(async () => (await host2.HistoryAsync(JobId))
                .Any(r => r.TriggerType == "follow-up" && r.Status == JobExecutionStatus.Completed));

            // It ran as the follow-up, not as a re-run of the original occurrence.
            Assert.True(secondState.Succeeded);
            var executed = Assert.Single(secondState.Attempts);
            Assert.Contains("followup-1", executed);

            // The queue is drained once the follow-up succeeded.
            await WorkerHost.WaitUntilAsync(async () =>
                await host2.PendingOccurrences().CountAsync(JobId) == 0);

            // The failure from the previous host is still on record.
            Assert.Contains(await host2.HistoryAsync(JobId), r => r.Status == JobExecutionStatus.Failed);
        }
    }

    [Fact]
    public async Task FollowUpsStopAtMaxAttempts()
    {
        using var database = new SqliteDatabase();
        var state = new FlippableState { ShouldFail = true };

        var host = await StartAsync(database, state, DateTimeOffset.UtcNow.AddSeconds(-5), maxAttempts: 2);
        await using (host)
        {
            // original + 2 follow-ups = 3 failed executions, then nothing more is queued.
            await WorkerHost.WaitUntilAsync(
                async () => await host.CountAsync(JobId, JobExecutionStatus.Failed) >= 3,
                TimeSpan.FromSeconds(30));

            await WorkerHost.WaitUntilAsync(async () => await host.PendingOccurrences().CountAsync(JobId) == 0);

            // Give the loop room to queue a fourth if it were going to.
            await Task.Delay(300);
            Assert.Equal(0, await host.PendingOccurrences().CountAsync(JobId));
            Assert.Equal(3, await host.CountAsync(JobId, JobExecutionStatus.Failed));
        }
    }

    [Fact]
    public async Task PermanentFailures_DoNotQueueAFollowUp_ByDefault()
    {
        using var database = new SqliteDatabase();

        var host = await WorkerHost.StartAsync(
            database,
            kit => kit.AddJob<PermanentlyFailingJob>(JobId, job => job
                .RunOnStartup()
                .WithRetryCount(0)
                .RetryLater(maxAttempts: 3, delay: TimeSpan.FromMilliseconds(200))),
            services => services.AddSingleton(new FlippableState()));

        await using (host)
        {
            await WorkerHost.WaitUntilAsync(async () => await host.CountAsync(JobId, JobExecutionStatus.Failed) >= 1);

            await Task.Delay(300);
            Assert.Equal(0, await host.PendingOccurrences().CountAsync(JobId));
        }
    }

    private sealed class PermanentlyFailingJob : IWorkerJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
            => throw new PermanentJobException("the request is malformed and will stay malformed");
    }

    private static Task<WorkerHost> StartAsync(
        SqliteDatabase database,
        FlippableState state,
        DateTimeOffset saleTime,
        int maxAttempts = 3)
        => WorkerHost.StartAsync(
            database,
            kit => kit.AddJob<FlippableJob>(JobId, job => job
                .AtTimes(saleTime)
                .PreventOverlappingExecutions()
                // No in-execution retries: every attempt here is a separate durable occurrence,
                // which is what the test is about.
                .WithRetryCount(0)
                .RetryLater(o =>
                {
                    o.MaxAttempts = maxAttempts;
                    o.Delay = TimeSpan.FromMilliseconds(200);
                    o.MaxDelay = TimeSpan.FromSeconds(1);
                })),
            services => services.AddSingleton(state));
}
