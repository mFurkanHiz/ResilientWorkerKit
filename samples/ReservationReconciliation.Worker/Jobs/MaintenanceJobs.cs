using ResilientWorkerKit;

namespace ReservationReconciliation.Worker.Jobs;

/// <summary>Runs every day at 02:00 Europe/Istanbul.</summary>
public sealed class DailyReconciliationJob : IWorkerJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation(
            "Daily reconciliation for {ScheduledLocalTime} ({TimeZoneId})",
            context.ScheduledLocalTime, context.TimeZoneId);
        return Task.CompletedTask;
    }
}

/// <summary>Runs every Sunday at 03:00 Europe/Istanbul.</summary>
public sealed class WeeklyCleanupJob : IWorkerJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Weekly cleanup running");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Runs on the 5th of every month at 10:30 Europe/Istanbul. The schedule identity
/// (<c>monthly-billing:2026-08</c>) guarantees at most one completed run per month, even
/// across host restarts and misfire recovery.
/// </summary>
public sealed class MonthlyBillingReconciliationJob : IWorkerJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation(
            "Monthly billing reconciliation for {ScheduledExecutionId}", context.ScheduledExecutionId);
        return Task.CompletedTask;
    }
}

/// <summary>Runs on the actual last day of every month at 23:00 Europe/Istanbul (Feb 28/29 correct).</summary>
public sealed class EndOfMonthSettlementJob : IWorkerJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation(
            "End-of-month settlement for {ScheduledLocalTime}", context.ScheduledLocalTime);
        return Task.CompletedTask;
    }
}
