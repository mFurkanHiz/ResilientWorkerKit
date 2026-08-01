using MultiJob.Worker.Jobs;
using ResilientWorkerKit;

var builder = Host.CreateApplicationBuilder(args);

// Six independent jobs with six different schedule types in one host. In-memory stores are
// fine here because this sample demonstrates scheduling and failure isolation, not durability
// (see ReservationReconciliation.Worker for the durable SQLite setup).
builder.Services.AddResilientWorkerKit(kit =>
{
    kit.AddJob<HeartbeatJob>("heartbeat", job => job
        .WithInterval(TimeSpan.FromSeconds(10))
        .RunOnStartup());

    // Fails twice per execution before succeeding — watch it retry with backoff while the
    // heartbeat keeps beating and the host stays up.
    kit.AddJob<FlakyImportJob>("flaky-import", job => job
        .WithFixedDelay(TimeSpan.FromSeconds(15))
        .WithRetry(r =>
        {
            r.MaxRetries = 3;
            r.BaseDelay = TimeSpan.FromSeconds(1);
        })
        .WithTimeout(TimeSpan.FromSeconds(30))
        .PreventOverlappingExecutions()
        .DeadLetterOnFailure());

    kit.AddJob<DailyDigestJob>("daily-digest", job => job
        .DailyAt(new TimeOnly(2, 0), "Europe/Istanbul"));

    kit.AddJob<WeeklyCleanupJob>("weekly-cleanup", job => job
        .WeeklyAt([DayOfWeek.Sunday], new TimeOnly(3, 0), "Europe/Istanbul"));

    kit.AddJob<MonthlyInvoiceJob>("monthly-invoice", job => job
        .MonthlyOnDay(5, new TimeOnly(10, 30), "Europe/Istanbul", MonthlyInvalidDayPolicy.SkipMonth)
        .PreventOverlappingExecutions()
        .WithTimeout(TimeSpan.FromMinutes(30))
        .WithRetryCount(3)
        .WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce));

    kit.AddJob<EndOfMonthSummaryJob>("end-of-month-summary", job => job
        .OnLastDayOfMonth(new TimeOnly(23, 0), "Europe/Istanbul"));
});

var host = builder.Build();
await host.RunAsync();
