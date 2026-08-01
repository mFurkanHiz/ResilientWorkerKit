using ResilientWorkerKit;

namespace MultiJob.Worker.Jobs;

/// <summary>Succeeds every time — proves other jobs' failures never touch it.</summary>
public sealed class HeartbeatJob : IWorkerJob
{
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Heartbeat OK (execution {ExecutionId})", context.ExecutionId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Simulates a flaky import: the first two attempts of every execution fail with a transient
/// error, the third succeeds. Demonstrates retry with backoff, stable ExecutionId across
/// attempts, and failure isolation.
/// </summary>
public sealed class FlakyImportJob : IWorkerJob
{
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.AttemptNumber < 3)
        {
            throw new TransientJobException(
                $"Simulated transient failure on attempt {context.AttemptNumber}");
        }

        context.Logger.LogInformation(
            "Import succeeded on attempt {AttemptNumber} (execution {ExecutionId})",
            context.AttemptNumber, context.ExecutionId);
        return Task.CompletedTask;
    }
}

/// <summary>Runs every day at 02:00 Europe/Istanbul.</summary>
public sealed class DailyDigestJob : IWorkerJob
{
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation(
            "Daily digest for {ScheduledLocalTime} ({TimeZoneId})",
            context.ScheduledLocalTime, context.TimeZoneId);
        return Task.CompletedTask;
    }
}

/// <summary>Runs every Sunday at 03:00 Europe/Istanbul.</summary>
public sealed class WeeklyCleanupJob : IWorkerJob
{
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Weekly cleanup running");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Runs on the 5th of each month at 10:30 Europe/Istanbul, at most once per month — the
/// occurrence identity (e.g. <c>monthly-invoice:2026-08</c>) survives restarts.
/// </summary>
public sealed class MonthlyInvoiceJob : IWorkerJob
{
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation(
            "Monthly invoicing for occurrence {ScheduledExecutionId}", context.ScheduledExecutionId);
        return Task.CompletedTask;
    }
}

/// <summary>Runs on the actual last day of each month (Feb 28/29 handled correctly).</summary>
public sealed class EndOfMonthSummaryJob : IWorkerJob
{
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation(
            "End-of-month summary for {ScheduledLocalTime}", context.ScheduledLocalTime);
        return Task.CompletedTask;
    }
}
