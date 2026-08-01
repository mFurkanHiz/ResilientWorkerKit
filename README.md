# ResilientWorkerKit

**Keep writing plain `BackgroundService` jobs — stop rewriting scheduling, retry, checkpointing, idempotency and health tracking in every project.**

[![CI](https://github.com/OWNER/ResilientWorkerKit/actions/workflows/ci.yml/badge.svg)](https://github.com/OWNER/ResilientWorkerKit/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)

ResilientWorkerKit is a lightweight reliability and execution layer for background jobs hosted in
a .NET Generic Host. You write the business logic; the kit owns the loop, the failure boundary,
the retry policy, the durable checkpoint, the idempotency gate and the execution history.

```csharp
public sealed class ReservationSyncJob(IReservationApiClient api, ReservationLedger ledger) : IWorkerJob
{
    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var checkpoint = await context.Checkpoints.GetAsync<SyncCheckpoint>(cancellationToken);
        var page = await api.GetReservationsAsync(checkpoint?.ContinuationToken, cancellationToken);

        foreach (var reservation in page.Items)
        {
            var key = $"reservation:{reservation.Id}:v{reservation.Version}";
            if (await context.Idempotency.TryAcquireAsync(key, cancellationToken) != IdempotencyAcquireResult.Acquired)
            {
                continue; // already processed — no second side effect
            }

            ledger.Reconcile(reservation);
            await context.Idempotency.MarkCompletedAsync(key, cancellationToken);
        }

        // Only after the whole page succeeded:
        await context.Checkpoints.SaveAsync(new SyncCheckpoint(page.NextContinuationToken), cancellationToken);
    }
}
```

```csharp
services.AddResilientWorkerKit(kit =>
{
    kit.UseEntityFrameworkCore(db => db.UseSqlite(connectionString));

    kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
        .WithInterval(TimeSpan.FromMinutes(5))
        .RunOnStartup()
        .WithTimeout(TimeSpan.FromMinutes(2))
        .PreventOverlappingExecutions()
        .WithRetryCount(3));
});
```

---

## The problem

A plain `BackgroundService` looks harmless:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    await DoWorkAsync(stoppingToken);
    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
}
```

…until production happens:

| What goes wrong | What it costs |
|---|---|
| `DoWorkAsync` throws | The exception escapes `ExecuteAsync`; the **whole host stops**. One transient blip becomes an outage until someone restarts the process. |
| A `continue` skips the delay | A 100% CPU busy loop hammering an upstream API thousands of times per second. |
| No retry | A one-second network hiccup loses an entire cycle of work. |
| No durable progress | A crash mid-batch restarts blind: reprocess everything, or lose it. |
| No idempotency | Every retry and every restart creates duplicate records, duplicate emails, duplicate charges. |
| No overlap guard | A slow run overlaps its successor; both process the same rows. |
| Wall-clock scheduling | DST transitions skip a day — or fire twice. |
| `catch { log.LogInformation(ex.Message); }` | Days of failures invisible to alerting, stack traces gone. |
| Shutdown mid-item | The stop signal lands between an external write and the local status flip — the exact window that produces duplicates on restart. |

Every row above came out of a [clean-room analysis of real worker fleets](docs/problem-analysis.md);
every one of them is addressed by a specific feature below.

## What ResilientWorkerKit does

- **Failure isolation by construction.** A job exception cannot reach the host. It is caught at
  the execution boundary, classified, recorded and logged with its stack trace — and the scheduler
  loop, the other jobs and the host keep running.
- **Retry that understands failures.** Transient/permanent/cancelled/timed-out/abandoned/misconfigured
  classification, exponential backoff with jitter, `Retry-After` support, attempt and total timeouts.
  The `ExecutionId` is stable across attempts, so history reads as one execution with N attempts.
- **Durable checkpoints and real resume.** Save progress after each successful batch; after a crash
  the next execution continues from there instead of starting over.
- **Idempotency with an atomic gate.** Concurrent acquisitions of the same key are settled by the
  database (composite primary key + concurrency token), not by a hopeful `if (!exists)`.
- **Nine schedule types, one host.** Interval, fixed delay, cron, daily, weekly, monthly (with
  explicit short-month policies), last-day-of-month, one-time, run-on-startup — each with its own
  time zone, timeout, retry, misfire and overlap policy.
- **Restart-safe calendar identity.** A monthly occurrence carries the identity
  `monthly-billing:2026-08`; once it has completed, no restart, misfire recovery or DST quirk can
  run it a second time.
- **Graceful shutdown.** Running jobs get the cancellation token, a configurable grace period, and
  a `Cancelled` status logged at Information — a clean stop is not an error. Nothing half-finished
  is ever marked successful.
- **Observability that works out of the box.** Structured logs with correlation scopes,
  `System.Diagnostics.Metrics` counters/histograms, `ActivitySource` tracing, and per-job health
  checks with stuck detection.

## What it is not

Not Hangfire (no dashboard, no fire-and-forget job queue). Not Quartz (no clustering, no scheduler
platform). Not Temporal or Durable Task (no distributed workflow orchestration). Not a message
queue. See [docs/scope.md](docs/scope.md) and [docs/limitations.md](docs/limitations.md).

**It does not claim exactly-once execution.** The contract is *at-least-once execution + durable
checkpoints + idempotent processing*, which is what an honest single-process system can guarantee
when it talks to external APIs. See [docs/execution-semantics.md](docs/execution-semantics.md).

## Architecture

```mermaid
graph TD
    A[ResilientWorkerKit.Abstractions] --> B[ResilientWorkerKit]
    B --> C[ResilientWorkerKit.EntityFrameworkCore]
    B --> D[ResilientWorkerKit.HealthChecks]
    A --> E[ResilientWorkerKit.Http]
```

One hosted service owns an independent scheduler loop per job:

```
WorkerKitHostedService
 ├─ startup recovery: stale Running executions → Abandoned
 ├─ JobScheduleLoop "reservation-sync"  ─┐
 ├─ JobScheduleLoop "notification-…"    ─┼─ each: occurrence → misfire policy → overlap policy
 ├─ JobScheduleLoop "monthly-billing"   ─┘        → JobRunner (DI scope, retry, timeouts,
 └─ graceful shutdown drain                          history, dead letters, metrics)
```

Details in [docs/architecture.md](docs/architecture.md).

## Getting started

```bash
dotnet add package ResilientWorkerKit
```

```csharp
using ResilientWorkerKit;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddResilientWorkerKit(kit =>
{
    kit.AddJob<CleanupJob>("cleanup", job => job.WithInterval(TimeSpan.FromMinutes(10)));
});

await builder.Build().RunAsync();
```

That is the whole setup. In-memory stores are registered by default so you can start immediately;
add `kit.UseEntityFrameworkCore(...)` when you want the state to survive a restart.

### Multiple jobs, multiple schedules

```csharp
services.AddResilientWorkerKit(kit =>
{
    kit.UseEntityFrameworkCore(db => db.UseSqlite(connectionString));

    kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
        .WithInterval(TimeSpan.FromMinutes(5))
        .RunOnStartup()
        .PreventOverlappingExecutions());

    kit.AddJob<NotificationDispatchJob>("notification-dispatch", job => job
        .WithFixedDelay(TimeSpan.FromMinutes(1))
        .WithTimeout(TimeSpan.FromSeconds(45)));

    kit.AddJob<DailyReconciliationJob>("daily-reconciliation", job => job
        .DailyAt(new TimeOnly(2, 0), "Europe/Istanbul"));

    kit.AddJob<WeeklyCleanupJob>("weekly-cleanup", job => job
        .WeeklyAt([DayOfWeek.Sunday], new TimeOnly(3, 0), "Europe/Istanbul"));

    kit.AddJob<MonthlyBillingJob>("monthly-billing", job => job
        .MonthlyOnDay(5, new TimeOnly(10, 30), "Europe/Istanbul", MonthlyInvalidDayPolicy.SkipMonth)
        .WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce)
        .WithTimeout(TimeSpan.FromMinutes(30)));

    kit.AddJob<EndOfMonthSettlementJob>("end-of-month-settlement", job => job
        .OnLastDayOfMonth(new TimeOnly(23, 0), "Europe/Istanbul"));
});
```

If `reservation-sync` starts failing every run, the other five keep their schedules exactly.

### Checkpoint and resume

```csharp
public sealed record SyncCheckpoint(string? ContinuationToken, int PagesProcessed);

var checkpoint = await context.Checkpoints.GetAsync<SyncCheckpoint>(cancellationToken)
    ?? new SyncCheckpoint(null, 0);

// ... process the page, commit local changes ...

await context.Checkpoints.SaveAsync(
    new SyncCheckpoint(page.NextContinuationToken, checkpoint.PagesProcessed + 1),
    cancellationToken);
```

A failed batch never advances the checkpoint, so the next execution retries exactly the work that
did not finish. [docs/checkpoints.md](docs/checkpoints.md)

### API integration

```csharp
services.AddResilientApiClient<IReservationApiClient, ReservationApiClient>("reservations", o =>
{
    o.BaseAddress = new Uri(configuration["ReservationApi:BaseUrl"]!);
    o.AttemptTimeout = TimeSpan.FromSeconds(10);
    o.EnableIdempotencyKey = true;   // Idempotency-Key on POST/PUT/PATCH, stable across retries
    o.ApiKeyHeaderName = "X-Api-Key"; // value comes from IApiKeyProvider, never from source
});
```

Pooled clients, mandatory timeouts, retry with `Retry-After`, circuit breaker, correlation IDs,
pagination helpers, and logging that records method/host/path/status/duration — never bodies,
query strings or credentials. [docs/api-integration.md](docs/api-integration.md)

### Health checks

```csharp
services.AddHealthChecks().AddResilientWorkerKit();
app.MapHealthChecks("/health");
```

A job that has never run is Healthy (deploying a new job should not page anyone); consecutive
failures degrade it; sustained failure or a stuck execution escalates.
[docs/health-checks.md](docs/health-checks.md)

## FAQ

<details>
<summary><b>What happens when a job throws?</b></summary>

The exception is caught at the execution boundary, classified, and the execution is recorded as
`Failed` with the full stack trace logged at Error level. If the failure is transient and retries
remain, the kit retries with backoff. **The host does not stop, the scheduler loop does not stop,
and no other job is affected.** At the next scheduled occurrence, the job runs again.
</details>

<details>
<summary><b>How does the kit resume after a restart?</b></summary>

Your job writes a checkpoint after each fully successful batch. On the next execution — including
the first one after a crash — `context.Checkpoints.GetAsync<T>()` returns exactly that state.
Items that were already processed are additionally protected by their idempotency records, so a
partially-processed batch replays without duplicating side effects.
</details>

<details>
<summary><b>Why do I need idempotency if I have checkpoints?</b></summary>

Because there is no distributed transaction between "call the external API" and "record that we
called it". A crash in that gap means the item comes back. The checkpoint tells you *where* to
resume; the idempotency record tells you *which items in that range are already done*.
</details>

<details>
<summary><b>How is the 31st handled in February?</b></summary>

Explicitly, by your choice: `SkipMonth` (no run in short months),
`RunOnLastAvailableDay` (runs Feb 28, or Feb 29 in leap years), or `FailConfiguration` (the
ambiguity is rejected at startup so nobody discovers it in production).
[docs/monthly-scheduling.md](docs/monthly-scheduling.md)
</details>

<details>
<summary><b>What if the host was down when a job was due?</b></summary>

The misfire policy decides: `Skip` (wait for the next occurrence — the default for calendar
schedules), `RunImmediatelyOnce` (run the missed occurrence once, restart-safe),
`RunIfWithinTolerance` (only if it is not too late), or `RescheduleFromNow` (interval/fixed-delay
only). A missed occurrence is never created twice, even if the host restarts repeatedly.
</details>

<details>
<summary><b>Can I swap the persistence provider?</b></summary>

Yes — five interfaces (`IJobCheckpointStore`, `IJobExecutionStore`, `IIdempotencyStore`,
`IDeadLetterStore`, `IJobLockProvider`). Register your own implementation instead of calling
`UseEntityFrameworkCore`. [docs/persistence.md](docs/persistence.md)
</details>

<details>
<summary><b>How is this different from Hangfire or Quartz?</b></summary>

| | ResilientWorkerKit | Hangfire | Quartz.NET |
|---|---|---|---|
| Primary model | Scheduled jobs you write as classes in your host | Enqueued background jobs with a dashboard | Full scheduler platform |
| Checkpoint/resume | Built in, first-class | Not a concept | Not a concept |
| Idempotency store | Built in | Not a concept | Not a concept |
| Dashboard | No (Phase 2) | Yes | Third-party |
| Clustering | No (single active instance in v1) | Yes | Yes |
| Footprint | Small library, five interfaces | Larger platform | Larger platform |

Use Hangfire when you need a dashboard and enqueued fire-and-forget jobs. Use Quartz when you need
clustering and a full scheduler platform. Use this when you already have a Worker Service, your
jobs are polling/sync/reconciliation loops, and what you actually lack is reliability plumbing.
</details>

## Samples

| Sample | Shows |
|---|---|
| [`samples/MultiJob.Worker`](samples/MultiJob.Worker) | Six jobs, six schedule types, one host; a deliberately flaky job retrying while a heartbeat job keeps succeeding |
| [`samples/ReservationReconciliation.Worker`](samples/ReservationReconciliation.Worker) | The full stack: SQLite persistence, an embedded fake API that scripts 500s, a 429 with `Retry-After`, an invalid record and a duplicate record; checkpoint resume, dead-lettering, health endpoint |

```bash
dotnet run --project samples/ReservationReconciliation.Worker
```

Then open `http://localhost:5210/` for per-job health snapshots and `/health` for the health check.
Run it twice: the second run reports `ledgerSideEffects: 0` because every reservation's idempotency
record survived in SQLite. Details in the [sample README](samples/ReservationReconciliation.Worker/README.md).

## Tests

```bash
dotnet test
```

| Suite | Count | Scope |
|---|---|---|
| Unit | 165 | Schedule math (DST gaps, ambiguous hours, leap years, invalid-day policies), retry backoff and jitter bounds, failure classification, runner execution/retry/checkpoint/idempotency, misfire and overlap policies, manual triggers, graceful shutdown, in-memory stores, health evaluation, HTTP handlers and masking, registration validation |
| Integration | 13 | Real Generic Host, real DI scopes, real SQLite file database that survives restarts, real HTTP server: the end-to-end failure→restart→resume scenario, `Retry-After` and permanent-400 handling, abandoned-execution recovery, monthly identity across restarts, the EF Core idempotency race, health checks through the real pipeline |

Schedule and engine tests run on `FakeTimeProvider`, so a month of scheduling is verified in
milliseconds — the whole suite finishes in about ten seconds.

Measured coverage across the five library assemblies (coverlet, Release build):
**86.6% lines, 78.8% branches, 94% methods**. The uncovered remainder is mostly log-message
plumbing and defensive store-failure paths. Coverage runs in CI and is published as an artifact.

## Documentation

| | |
|---|---|
| [problem-analysis.md](docs/problem-analysis.md) | The observed failure patterns this library exists to remove |
| [architecture.md](docs/architecture.md) | Layers, components, execution and scheduler lifecycles |
| [scope.md](docs/scope.md) | MVP, non-goals, Phase 2 |
| [public-api.md](docs/public-api.md) | Job API, registration API, store API, extension points |
| [execution-semantics.md](docs/execution-semantics.md) | At-least-once, identities, states, the exactly-once limitation |
| [scheduling.md](docs/scheduling.md) | Every schedule type, time zones, misfire, overlap |
| [monthly-scheduling.md](docs/monthly-scheduling.md) | Day-of-month, short months, leap years, restart behavior |
| [checkpoints.md](docs/checkpoints.md) | Checkpoint shapes, atomicity, the transaction boundary |
| [idempotency.md](docs/idempotency.md) | Keys, races, record lifecycle, security |
| [failure-handling.md](docs/failure-handling.md) | Classification, retry, timeouts, cancellation, dead letters |
| [api-integration.md](docs/api-integration.md) | Typed clients, auth, resilience, pagination, masking |
| [persistence.md](docs/persistence.md) | In-memory, EF Core, SQLite, SQL Server, migrations |
| [observability.md](docs/observability.md) | Logs, metrics, traces, cardinality rules |
| [health-checks.md](docs/health-checks.md) | Status computation, thresholds, stuck detection |
| [security.md](docs/security.md) | Secrets, logs, checkpoints, dead letters, packaging |
| [testing.md](docs/testing.md) | How the kit is tested and how to test your own jobs |
| [limitations.md](docs/limitations.md) | What this does not do |
| [roadmap.md](docs/roadmap.md) | Distributed locks, admin API, dashboard, NuGet publication |

## Known limitations

- **No exactly-once execution.** At-least-once + checkpoints + idempotency, by design.
- **Single active host instance.** v1 locking is in-process; running two instances against one
  database can double-execute. Distributed locking is the top Phase 2 item.
- **No dashboard or admin API.** A manual-trigger extension point (`IManualJobTrigger`) exists;
  the HTTP surface around it does not.
- **Job definitions live in code**, not in the database — no runtime registration.
- **Not a workflow engine.** No inter-job dependencies, fan-out/fan-in or compensation.

## Requirements

.NET 8.0 or later. Targets `net8.0` only: it is the LTS, it runs on .NET 8/9/10 hosts, and
multi-targeting would add build complexity without a functional benefit today.

## License

MIT — see [LICENSE](LICENSE).
