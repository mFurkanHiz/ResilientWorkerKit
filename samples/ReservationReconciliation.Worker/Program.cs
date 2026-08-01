using Microsoft.EntityFrameworkCore;
using ReservationReconciliation.Worker.Api;
using ReservationReconciliation.Worker.Domain;
using ReservationReconciliation.Worker.Jobs;
using ResilientWorkerKit;
using ResilientWorkerKit.EntityFrameworkCore;
using ResilientWorkerKit.HealthChecks;
using ResilientWorkerKit.Http;

var builder = WebApplication.CreateBuilder(args);

var listenUrl = builder.Configuration["Sample:ListenUrl"] ?? "http://localhost:5210";
builder.WebHost.UseUrls(listenUrl);

// Sample domain services + the embedded fake API state.
builder.Services.AddSingleton<ReservationLedger>();
builder.Services.AddSingleton<FakeReservationApiState>();
builder.Services.AddSingleton<NotificationInbox>();

// Typed API client with the full resilient pipeline. It points at this very process's
// embedded fake API, which scripts 500s, a 429 with Retry-After, an invalid record and a
// duplicate record (see FakeReservationApiState).
builder.Services.AddResilientApiClient<IReservationApiClient, ReservationApiClient>("reservations", options =>
{
    options.BaseAddress = new Uri($"{listenUrl.TrimEnd('/')}/fake-api/");
    options.AttemptTimeout = TimeSpan.FromSeconds(5);
    options.TotalTimeout = TimeSpan.FromSeconds(30);
    options.EnableIdempotencyKey = true;
});

// The worker kit: six jobs, durable SQLite persistence, per-job health tracking.
builder.Services.AddResilientWorkerKit(kit =>
{
    kit.Options.ShutdownGracePeriod = TimeSpan.FromSeconds(15);

    kit.UseEntityFrameworkCore(
        db => db.UseSqlite(builder.Configuration.GetConnectionString("WorkerKit")
            ?? "Data Source=reservation-reconciliation.db"),
        ef => ef.AutoCreateSchema = true); // demo convenience; use migrations in production

    kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
        .WithInterval(TimeSpan.FromMinutes(5))
        .RunOnStartup()
        .WithTimeout(TimeSpan.FromMinutes(2))
        .PreventOverlappingExecutions()
        .WithRetry(r =>
        {
            r.MaxRetries = 3;
            r.BaseDelay = TimeSpan.FromSeconds(2);
        })
        .DeadLetterOnFailure());

    kit.AddJob<NotificationDispatchJob>("notification-dispatch", job => job
        .WithFixedDelay(TimeSpan.FromMinutes(1))
        .RunOnStartup()
        .WithTimeout(TimeSpan.FromSeconds(45)));

    kit.AddJob<DailyReconciliationJob>("daily-reconciliation", job => job
        .DailyAt(new TimeOnly(2, 0), "Europe/Istanbul"));

    kit.AddJob<WeeklyCleanupJob>("weekly-cleanup", job => job
        .WeeklyAt([DayOfWeek.Sunday], new TimeOnly(3, 0), "Europe/Istanbul"));

    kit.AddJob<MonthlyBillingReconciliationJob>("monthly-billing", job => job
        .MonthlyOnDay(5, new TimeOnly(10, 30), "Europe/Istanbul", MonthlyInvalidDayPolicy.SkipMonth)
        .PreventOverlappingExecutions()
        .WithTimeout(TimeSpan.FromMinutes(30))
        .WithRetryCount(3)
        .WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce));

    kit.AddJob<EndOfMonthSettlementJob>("end-of-month-settlement", job => job
        .OnLastDayOfMonth(new TimeOnly(23, 0), "Europe/Istanbul"));
});

builder.Services.AddHealthChecks().AddResilientWorkerKit();

var app = builder.Build();

app.MapFakeReservationApi();
app.MapHealthChecks("/health");

// A small status endpoint showing per-job health snapshots and the demo counters.
app.MapGet("/", (IJobHealthTracker tracker, ReservationLedger ledger, NotificationInbox inbox) => Results.Ok(new
{
    ledgerSideEffects = ledger.SideEffectCount,
    reconciledReservations = ledger.Reservations.Count,
    notificationsReceivedByFakeApi = inbox.Received,
    jobs = tracker.GetAll().Select(s => new
    {
        s.JobId,
        s.IsRunning,
        lastResult = s.LastResult?.ToString(),
        s.ConsecutiveFailures,
        s.LastSuccessAtUtc,
        s.NextOccurrenceUtc,
        s.LastProgress,
        s.LastCheckpointSummary,
    }),
}));

app.Run();
