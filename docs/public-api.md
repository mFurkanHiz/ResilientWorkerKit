# Public API

The public surface is deliberately small. A user must learn **one interface** (`IWorkerJob`),
**one context type** (`JobExecutionContext`) and **one registration call**
(`AddResilientWorkerKit`). Everything else is optional.

## Job API

```csharp
public interface IWorkerJob
{
    Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}
```

`JobExecutionContext` (all members read-only from job code):

| Member | Purpose |
|---|---|
| `JobId`, `DisplayName` | Stable job identity |
| `ExecutionId` | Stable across retry attempts of one execution |
| `ScheduledExecutionId` | Identity of the schedule occurrence (e.g. `monthly-billing:2026-08`) |
| `AttemptNumber` | 1-based; incremented per retry |
| `ScheduledAtUtc`, `ScheduledLocalTime`, `TimeZoneId` | When and in which zone the occurrence was planned |
| `StartedAtUtc` | Actual start (UTC) |
| `CorrelationId`, `HostInstanceId` | Tracing/diagnostics identity |
| `Services` | **Scoped** `IServiceProvider` (a new DI scope per execution — never the root provider) |
| `Logger` | Job-scoped `ILogger` with structured scope (JobId/ExecutionId/Attempt...) |
| `Checkpoints` | `IJobCheckpointAccessor` — typed get/save/clear |
| `Idempotency` | `IJobIdempotencyAccessor` — exists/try-acquire/complete/fail |
| `DeadLetters` | `IJobDeadLetterAccessor` — item-level dead-letter records |
| `Items` | Per-execution scratch dictionary |
| `CancellationToken` | Same token passed to `ExecuteAsync` |
| `ReportProgress(string)` | Safe progress note → health snapshot + debug log |

Accessors:

```csharp
public interface IJobCheckpointAccessor
{
    Task<T?> GetAsync<T>(CancellationToken ct = default);
    Task SaveAsync<T>(T checkpoint, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public interface IJobIdempotencyAccessor
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);              // completed?
    Task<IdempotencyAcquireResult> TryAcquireAsync(string key, CancellationToken ct = default);
    Task MarkCompletedAsync(string key, CancellationToken ct = default);
    Task MarkFailedAsync(string key, CancellationToken ct = default);
}
// IdempotencyAcquireResult: Acquired | AlreadyCompleted | InProgressElsewhere
```

## Registration API

```csharp
services.AddResilientWorkerKit(kit =>
{
    kit.Options.HostInstanceId = Environment.MachineName;      // optional
    kit.Options.ShutdownGracePeriod = TimeSpan.FromSeconds(30);

    kit.AddJob<ReservationSyncJob>("reservation-sync", job =>
    {
        job.WithInterval(TimeSpan.FromMinutes(5));
        job.RunOnStartup();
        job.WithTimeout(TimeSpan.FromMinutes(2));
        job.PreventOverlappingExecutions();                    // SkipNewExecution
        job.WithRetry(r => { r.MaxRetries = 3; r.BaseDelay = TimeSpan.FromSeconds(2); });
    });

    kit.AddJob<MonthlyBillingJob>("monthly-billing", job =>
    {
        job.MonthlyOnDay(5, new TimeOnly(10, 30), "Europe/Istanbul",
                         MonthlyInvalidDayPolicy.SkipMonth);
        job.WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce);
    });
});
```

`JobBuilder` methods (each returns the builder):

- Schedules: `WithInterval(TimeSpan)`, `WithFixedDelay(TimeSpan)`, `WithCron(string)`,
  `DailyAt(TimeOnly, string timeZone)`, `WeeklyAt(DayOfWeek[], TimeOnly, string timeZone)`,
  `MonthlyOnDay(int day, TimeOnly, string timeZone, MonthlyInvalidDayPolicy)`,
  `OnLastDayOfMonth(TimeOnly, string timeZone)`, `OnceAt(DateTimeOffset)`,
  `WithSchedule(IJobSchedule)` (escape hatch), `RunOnStartup()`
- Policies: `WithTimeout(TimeSpan)` (total), `WithRetry(Action<JobRetryOptions>)`,
  `WithRetryCount(int)`, `PreventOverlappingExecutions(OverlapPolicy = SkipNewExecution)`,
  `WithMisfirePolicy(MisfirePolicy, TimeSpan? tolerance = null)`,
  `WithTimeZone(string)` (default zone for schedule types that take none),
  `DeadLetterOnFailure()`, `Disabled()`, `WithDisplayName(string)`,
  `WithHealthThresholds(Action<JobHealthThresholds>)`

All configuration is validated when the registry is built, before any job runs: unknown time
zones, day-of-month out of range, negative timeouts and retry values, two schedules on one job,
`RunIfWithinTolerance` without a tolerance, `RescheduleFromNow` on a calendar schedule, and
duplicate JobIds all throw `JobConfigurationException`.

A job **without** a schedule is legal by design: it never fires on its own and runs only via
`RunOnStartup()` and/or `IManualJobTrigger`.

## Store API (extension points)

All durable state flows through five interfaces; in-memory implementations are registered by
default and replaced by calling e.g. `kit.UseEntityFrameworkCore(...)`:

```csharp
IJobCheckpointStore   // one JSON checkpoint per job
IJobExecutionStore    // execution history + ScheduledExecutionId dedup + abandon recovery
IIdempotencyStore     // atomic TryAcquire / MarkCompleted / MarkFailed with expiry
IDeadLetterStore      // execution- and item-level dead letters
IJobLockProvider      // per-job overlap lock (in-process default; distributed = Phase 2)
```

Other extension points:

- `IJobFailureClassifier` — map exceptions to `JobFailureKind` (+ optional retry-after hint).
  The default classifier understands `IJobFailureHint` (implemented by
  `TransientJobException`, `PermanentJobException` and `ResilientWorkerKit.Http`'s
  `ApiRequestException`), `OperationCanceledException` and `TimeoutException`.
- `IJobSchedule` — custom schedule types.
- `IManualJobTrigger` — trigger a job on demand (admin API / tests). Returns the ExecutionId.
- `IJobHealthTracker` — read-only per-job health snapshots (used by the HealthChecks package).
- `TimeProvider` — the engine takes the ambient `TimeProvider` from DI; tests inject
  `FakeTimeProvider`.

## HTTP package

```csharp
services.AddResilientApiClient<IReservationApiClient, ReservationApiClient>("reservations", o =>
{
    o.BaseAddress = new Uri(builder.Configuration["ReservationApi:BaseUrl"]!);
    o.AttemptTimeout = TimeSpan.FromSeconds(10);
    o.EnableCorrelationId = true;      // X-Correlation-ID
    o.EnableIdempotencyKey = true;     // Idempotency-Key on POST/PUT/PATCH
    o.ApiKeyHeaderName = "X-Api-Key";  // value comes from IApiKeyProvider (never from source)
});
```

Building blocks: `IBearerTokenProvider` (+ `CachingBearerTokenProvider`), `IApiKeyProvider`,
`ContinuationPage<T>` / `CursorPage<T>` + `PageReader`, `ApiRequestException : IJobFailureHint`,
`HttpResponseMessageExtensions.EnsureApiSuccessAsync()`, `SensitiveDataMasker`.
Resilience (retry, circuit breaker, timeout, rate limiter, `Retry-After`) comes from
`Microsoft.Extensions.Http.Resilience`'s standard pipeline, customizable per client.

## Health checks package

```csharp
services.AddHealthChecks().AddResilientWorkerKit();   // one aggregate check, per-job entries
```

## Compatibility promise

Types under `*.Internal` namespaces (and `internal` types) carry no compatibility guarantees.
Everything else follows semantic versioning once 1.0 ships.
