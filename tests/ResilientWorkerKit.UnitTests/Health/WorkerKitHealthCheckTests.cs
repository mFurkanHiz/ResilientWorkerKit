using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using ResilientWorkerKit.Engine;
using ResilientWorkerKit.HealthChecks;
using ResilientWorkerKit.Registration;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Health;

public class WorkerKitHealthCheckTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-01T10:00:00Z"));
    private readonly JobHealthTracker _tracker = new();

    private WorkerKitHealthCheck Check(params JobDefinition[] definitions)
    {
        foreach (var definition in definitions)
        {
            _tracker.RegisterJob(definition);
        }

        return new WorkerKitHealthCheck(_tracker, new JobRegistry(definitions), _time);
    }

    private static Task<HealthCheckResult> Run(WorkerKitHealthCheck check)
        => check.CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task NeverRunJob_IsHealthy_NotUnhealthy()
    {
        var check = Check(RunnerHarness.Definition(jobId: "new-job"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("not yet run", result.Data["new-job"].ToString());
    }

    [Fact]
    public async Task SuccessfulJob_IsHealthy()
    {
        var check = Check(RunnerHarness.Definition(jobId: "ok-job"));
        var now = _time.GetUtcNow();
        _tracker.OnExecutionStarted("ok-job", now, now);
        _tracker.OnExecutionFinished("ok-job", JobExecutionStatus.Completed, now.AddSeconds(1), 1000);

        var result = await Run(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ConsecutiveFailures_ReportDegraded()
    {
        var check = Check(RunnerHarness.Definition(jobId: "flaky-job"));
        var now = _time.GetUtcNow();
        for (var i = 0; i < 2; i++)
        {
            _tracker.OnExecutionStarted("flaky-job", now, now);
            _tracker.OnExecutionFinished("flaky-job", JobExecutionStatus.Failed, now, 10);
        }

        var result = await Run(check);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task SustainedFailures_ReportUnhealthy()
    {
        var check = Check(RunnerHarness.Definition(jobId: "dead-job"));
        var now = _time.GetUtcNow();
        for (var i = 0; i < 5; i++)
        {
            _tracker.OnExecutionStarted("dead-job", now, now);
            _tracker.OnExecutionFinished("dead-job", JobExecutionStatus.Failed, now, 10);
        }

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task SuccessResetsTheFailureStreak()
    {
        var check = Check(RunnerHarness.Definition(jobId: "recovered-job"));
        var now = _time.GetUtcNow();
        for (var i = 0; i < 4; i++)
        {
            _tracker.OnExecutionStarted("recovered-job", now, now);
            _tracker.OnExecutionFinished("recovered-job", JobExecutionStatus.Failed, now, 10);
        }

        _tracker.OnExecutionStarted("recovered-job", now, now);
        _tracker.OnExecutionFinished("recovered-job", JobExecutionStatus.Completed, now, 10);

        var result = await Run(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task NoSuccessForConfiguredWindow_ReportsUnhealthy()
    {
        var check = Check(RunnerHarness.Definition(
            b => b.WithHealthThresholds(t =>
            {
                t.UnhealthyWhenNoSuccessFor = TimeSpan.FromHours(1);
                t.DegradedAfterConsecutiveFailures = 1;
            }),
            jobId: "stale-job"));

        var start = _time.GetUtcNow();
        _tracker.OnExecutionStarted("stale-job", start, start);
        _tracker.OnExecutionFinished("stale-job", JobExecutionStatus.Completed, start, 10);
        _tracker.OnExecutionStarted("stale-job", start.AddMinutes(5), start.AddMinutes(5));
        _tracker.OnExecutionFinished("stale-job", JobExecutionStatus.Failed, start.AddMinutes(5), 10);

        _time.Advance(TimeSpan.FromHours(2));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("no successful execution", result.Data["stale-job"].ToString());
    }

    [Fact]
    public async Task LongRunningExecution_IsReportedAsPossiblyStuck()
    {
        var check = Check(RunnerHarness.Definition(
            b => b.WithHealthThresholds(t => t.StuckAfter = TimeSpan.FromMinutes(10)),
            jobId: "stuck-job"));

        var start = _time.GetUtcNow();
        _tracker.OnExecutionStarted("stuck-job", start, start);
        _time.Advance(TimeSpan.FromMinutes(30));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("stuck", result.Data["stuck-job"].ToString());
    }

    [Fact]
    public async Task RunningStateAndNextOccurrence_AreReported()
    {
        var check = Check(RunnerHarness.Definition(jobId: "live-job"));
        var now = _time.GetUtcNow();
        _tracker.OnExecutionStarted("live-job", now, now);
        _tracker.OnNextOccurrence("live-job", now.AddMinutes(5));

        var result = await Run(check);

        var snapshot = _tracker.Get("live-job")!;
        Assert.True(snapshot.IsRunning);
        Assert.Equal(now.AddMinutes(5), snapshot.NextOccurrenceUtc);
        Assert.Contains("running", result.Data["live-job"].ToString());
    }

    [Fact]
    public async Task DisabledJobs_AreSkipped()
    {
        var check = Check(RunnerHarness.Definition(b => b.Disabled(), jobId: "off-job"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("disabled", result.Data["off-job"]);
    }
}
