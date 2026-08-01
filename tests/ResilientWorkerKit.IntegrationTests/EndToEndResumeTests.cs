using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ResilientWorkerKit.Http;
using ResilientWorkerKit.IntegrationTests.Infrastructure;

namespace ResilientWorkerKit.IntegrationTests;

/// <summary>
/// The flagship scenario from docs/test-plan.md, run against a real Generic Host, real DI
/// scopes, a real HTTP server and a real SQLite file that survives the restart.
/// </summary>
public class EndToEndResumeTests
{
    private const string SyncJobId = "paged-sync";
    private const string OtherJobId = "other-job";

    private static readonly SyncPage Page1 = new([new SyncItem(1, 1), new SyncItem(2, 1)], "page-2");
    private static readonly SyncPage Page2 = new([new SyncItem(1, 1), new SyncItem(3, 1)], null);

    [Fact]
    public async Task Sync_FailsOnPage2_HostSurvives_ThenResumesFromCheckpointAfterRestart()
    {
        using var database = new SqliteDatabase();
        var page2ShouldFail = true;

        await using var api = new FakeApiServer(request =>
        {
            var token = request.QueryString["continuationToken"];
            if (string.IsNullOrEmpty(token))
            {
                return FakeApiResponse.Json(Page1);
            }

            if (token == "page-2" && Volatile.Read(ref page2ShouldFail))
            {
                return new FakeApiResponse(500, """{"title":"simulated outage"}""");
            }

            return FakeApiResponse.Json(Page2);
        });

        var ledger = new SideEffectLedger();
        var otherJobCounter = new SucceedingJobCounter();

        // ---- Host #1: page 1 succeeds, page 2 exhausts its retries -------------------------
        var host1 = await StartHostAsync(database, api, ledger, otherJobCounter);
        await using (host1)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host1.CountAsync(SyncJobId, JobExecutionStatus.Failed) >= 1);

            // Page 1's items were applied; the checkpoint stopped exactly at page 2.
            Assert.Equal(2, ledger.Count);
            var checkpoint = await host1.Checkpoints().GetAsync(SyncJobId);
            Assert.NotNull(checkpoint);
            Assert.Contains("page-2", checkpoint.PayloadJson);

            // The host survived and the other job kept running.
            await WorkerHost.WaitUntilAsync(async () =>
                await host1.CountAsync(OtherJobId, JobExecutionStatus.Completed) >= 1);
            Assert.True(otherJobCounter.Count >= 1);

            var failure = (await host1.HistoryAsync(SyncJobId)).First(r => r.Status == JobExecutionStatus.Failed);
            Assert.Equal(JobFailureKind.Transient, failure.FailureKind);
            Assert.Equal(3, failure.AttemptCount); // 1 initial + 2 retries

            // The recorded message stays diagnostic-but-safe: status and path, plus the
            // ProblemDetails title, and never the query string that carried the token.
            Assert.Contains("500", failure.ErrorMessage);
            Assert.Contains("/items", failure.ErrorMessage);
            Assert.DoesNotContain("continuationToken", failure.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
            Assert.NotNull(failure.ErrorDetail);

            // A dead letter recorded the exhausted execution.
            Assert.NotEmpty(await host1.DeadLetters().GetPendingAsync(SyncJobId, 10));
        }

        // ---- Host #2: same database, upstream healthy again --------------------------------
        Volatile.Write(ref page2ShouldFail, false);
        var restartLedger = new SideEffectLedger();
        var host2 = await StartHostAsync(database, api, restartLedger, new SucceedingJobCounter());
        await using (host2)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host2.CountAsync(SyncJobId, JobExecutionStatus.Completed) >= 1);

            // Resumed at page 2 — item 1 (already applied before the crash) produced no second
            // side effect; only the genuinely new item 3 did.
            Assert.Equal(["3:v1"], restartLedger.Applied);

            // The checkpoint was reset after the final page.
            var checkpoint = await host2.Checkpoints().GetAsync(SyncJobId);
            Assert.Contains("null", checkpoint!.PayloadJson);

            // History from host #1 is still there: durable across restarts.
            var history = await host2.HistoryAsync(SyncJobId);
            Assert.Contains(history, r => r.Status == JobExecutionStatus.Failed);
            Assert.Contains(history, r => r.Status == JobExecutionStatus.Completed);
        }
    }

    [Fact]
    public async Task RetryAfterHeader_IsHonored_And400IsNotRetried()
    {
        using var database = new SqliteDatabase();
        var calls = 0;

        await using var api = new FakeApiServer(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            return call switch
            {
                1 => FakeApiResponse.Status(429, new KeyValuePair<string, string>("Retry-After", "1")),
                2 => new FakeApiResponse(400, """{"title":"permanently invalid request"}"""),
                _ => FakeApiResponse.Json(new SyncPage([], null)),
            };
        });

        var host = await StartHostAsync(database, api, new SideEffectLedger(), new SucceedingJobCounter());
        await using (host)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host.CountAsync(SyncJobId, JobExecutionStatus.Failed) >= 1);

            var failure = (await host.HistoryAsync(SyncJobId)).First(r => r.Status == JobExecutionStatus.Failed);

            // 429 → retried (attempt 2), 400 → permanent, so exactly 2 attempts and no more.
            // This assertion is also the canary for store-readiness bugs: an engine that starts
            // before its schema exists fails an extra attempt here without making an HTTP call.
            Assert.Equal(2, failure.AttemptCount);
            Assert.Equal(JobFailureKind.Permanent, failure.FailureKind);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
    }

    [Fact]
    public async Task CrashedRunningExecution_IsMarkedAbandonedOnNextStartup()
    {
        using var database = new SqliteDatabase();
        await using var api = new FakeApiServer(_ => FakeApiResponse.Json(new SyncPage([], null)));

        // Seed a "Running" record as a crashed process would leave behind.
        var seedHost = await StartHostAsync(database, api, new SideEffectLedger(), new SucceedingJobCounter(), enableJobs: false);
        await using (seedHost)
        {
            await seedHost.Executions().CreateAsync(new JobExecutionRecord
            {
                JobId = SyncJobId,
                ExecutionId = "crashed-execution",
                ScheduledExecutionId = $"{SyncJobId}:crashed",
                ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                Status = JobExecutionStatus.Running,
            });
        }

        var host = await StartHostAsync(database, api, new SideEffectLedger(), new SucceedingJobCounter());
        await using (host)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                (await host.Executions().GetAsync("crashed-execution"))?.Status == JobExecutionStatus.Abandoned);

            var record = await host.Executions().GetAsync("crashed-execution");
            Assert.Equal(JobExecutionStatus.Abandoned, record!.Status);
            Assert.Equal(JobFailureKind.Abandoned, record.FailureKind);
        }
    }

    [Fact]
    public async Task IdempotencyRecords_SurviveRestart_AndPreventDuplicateSideEffects()
    {
        using var database = new SqliteDatabase();
        var singlePage = new SyncPage([new SyncItem(7, 1), new SyncItem(8, 1)], null);
        await using var api = new FakeApiServer(_ => FakeApiResponse.Json(singlePage));

        var firstLedger = new SideEffectLedger();
        var host1 = await StartHostAsync(database, api, firstLedger, new SucceedingJobCounter());
        await using (host1)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host1.CountAsync(SyncJobId, JobExecutionStatus.Completed) >= 1);
        }

        Assert.Equal(2, firstLedger.Count);

        var secondLedger = new SideEffectLedger();
        var host2 = await StartHostAsync(database, api, secondLedger, new SucceedingJobCounter());
        await using (host2)
        {
            await WorkerHost.WaitUntilAsync(async () =>
                await host2.CountAsync(SyncJobId, JobExecutionStatus.Completed) >= 1);
        }

        // The same two items came back; both were suppressed by their persisted idempotency records.
        Assert.Empty(secondLedger.Applied);
    }

    private static async Task<WorkerHost> StartHostAsync(
        SqliteDatabase database,
        FakeApiServer api,
        SideEffectLedger ledger,
        SucceedingJobCounter counter,
        bool enableJobs = true)
        => await WorkerHost.StartAsync(
            database,
            kit =>
            {
                kit.AddJob<PagedSyncJob>(SyncJobId, job =>
                {
                    job.WithInterval(TimeSpan.FromMinutes(30))
                        .RunOnStartup()
                        .WithTimeout(TimeSpan.FromSeconds(30))
                        .PreventOverlappingExecutions()
                        .DeadLetterOnFailure()
                        .WithRetry(r =>
                        {
                            r.MaxRetries = 2;
                            r.BaseDelay = TimeSpan.FromMilliseconds(20);
                            r.MaxDelay = TimeSpan.FromMilliseconds(50);
                            r.JitterFactor = 0;
                        });
                    if (!enableJobs)
                    {
                        job.Disabled();
                    }
                });

                kit.AddJob<SucceedingJob>(OtherJobId, job =>
                {
                    job.WithInterval(TimeSpan.FromSeconds(1)).RunOnStartup();
                    if (!enableJobs)
                    {
                        job.Disabled();
                    }
                });
            },
            services =>
            {
                services.AddSingleton(ledger);
                services.AddSingleton(counter);
                services.AddResilientApiClient<SyncApiMarker, SyncApiMarker>("sync-api", o =>
                {
                    o.BaseAddress = api.BaseAddress;
                    o.AttemptTimeout = TimeSpan.FromSeconds(5);
                    o.TotalTimeout = TimeSpan.FromSeconds(10);
                    // HTTP-level retries are switched off so that every attempt is a job
                    // attempt: the assertions below then observe the engine's retry behavior
                    // directly in the execution record.
                    o.ConfigureResilience = resilience =>
                    {
                        resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                        resilience.CircuitBreaker.ShouldHandle = _ => ValueTask.FromResult(false);
                    };
                });
            });

    /// <summary>
    /// Typed-client marker: the job resolves the named <c>sync-api</c> client from
    /// <see cref="IHttpClientFactory"/>, so retries are owned by the job engine (not the HTTP
    /// pipeline) and every attempt is visible in the execution record.
    /// </summary>
    internal sealed class SyncApiMarker
    {
        public SyncApiMarker(HttpClient httpClient) => HttpClient = httpClient;

        public HttpClient HttpClient { get; }
    }
}
