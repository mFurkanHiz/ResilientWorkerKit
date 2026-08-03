# Scope

## Positioning

ResilientWorkerKit is a **lightweight reliability and execution layer** for .NET developers who
want to keep writing plain `BackgroundService`-hosted jobs, but do not want to re-implement
scheduling, retry, checkpointing, idempotency, health checks and execution tracking in every
project.

It is intentionally **not**:

- a Hangfire alternative (no dashboard, no fire-and-forget job queue, no distributed job storage contract),
- a Quartz alternative (no full scheduler platform, no clustering, no plugin ecosystem),
- a Durable Task / Temporal alternative (no distributed workflow orchestration, no replay-based determinism),
- a message queue or an event bus,
- a general-purpose distributed scheduler.

The unit of work is a **job class you write** (`IWorkerJob`), executed **in-process** inside a
single .NET Generic Host, with durable state (checkpoints, execution history, idempotency
records) stored through pluggable providers.

## Core scope (shipped since 1.0)

### Execution engine
- `IWorkerJob` job contract with `JobExecutionContext`
- Per-job scheduler loops inside one hosted service; failure isolation between jobs
- At-least-once execution semantics with durable checkpoints and idempotency (documented; no exactly-once claim)
- Retry with failure classification (transient / permanent / cancelled / timed-out / abandoned / misconfigured),
  exponential backoff + jitter, `Retry-After` hints, attempt timeout, total execution timeout
- Same `ExecutionId` retained across retry attempts; new schedule occurrence ⇒ new `ExecutionId`
- Overlap policies: `SkipNewExecution` (default), `QueueSingleExecution`, `AllowConcurrentExecutions`
- In-process per-job locking (`IJobLockProvider` abstraction; distributed lock is Phase 2)
- Graceful shutdown: grace period, correct `Cancelled`/`Abandoned` statuses, no false error logs
- Crash recovery: `Running` records left behind by a crashed process are marked `Abandoned` at startup
- Manual trigger extension point (`IManualJobTrigger`)

### Scheduling
- Interval, fixed-delay, cron (Cronos), daily, weekly, monthly (day-of-month with
  `SkipMonth` / `RunOnLastAvailableDay` / `FailConfiguration` invalid-day policies),
  last-day-of-month, one-time, run-on-startup flag
- Per-job IANA time zone; DST-safe occurrence calculation via `TimeProvider`
- Scheduled execution identity (e.g. `monthly-billing:2026-08`) preventing duplicate monthly runs across restarts
- Misfire policies: `Skip`, `RunImmediatelyOnce`, `RunIfWithinTolerance`, `RescheduleFromNow` (per-type support documented)

### Persistence
- In-memory stores (tests/demo only — documented as not production-suitable)
- EF Core stores (SQLite sample, SQL Server compatible model): executions, checkpoints,
  idempotency records, dead letters

### HTTP integration
- Typed `HttpClient` registration built on `IHttpClientFactory` + `Microsoft.Extensions.Http.Resilience`
  (retry, circuit breaker, timeout, rate limiter, `Retry-After`)
- Correlation-ID and Idempotency-Key propagation, API-key and bearer-token handlers with caching extension point
- Safe (masked) request/response metadata logging; no bodies, no secrets
- Continuation-token / cursor pagination helpers; safe API error model; `IJobFailureHint` bridge into retry classification

### Observability & health
- Structured logging (`Microsoft.Extensions.Logging`, source-generated log messages)
- Metrics via `System.Diagnostics.Metrics` (`Meter` name `ResilientWorkerKit`), tracing via `ActivitySource`
  — consumable by OpenTelemetry without a dedicated adapter package
- Health check package: per-job Healthy/Degraded/Unhealthy with configurable thresholds and stuck detection

## Non-goals (Core)

- Exactly-once execution guarantees
- Distributed locking / multi-instance coordination (single active host instance is assumed; documented)
- Web dashboard or admin UI
- Job persistence of *job definitions* (definitions live in code; only runtime state is persisted)
- Dynamic job registration at runtime — intentionally outside the Core package; runtime-defined
  schedules are developed through optional Dynamic Scheduling packages in the same repository
- Message-queue-driven job triggering
- PostgreSQL/MySQL-specific store packages (the EF Core model is provider-agnostic; only SQLite is shipped as a sample provider)

## Phase 2 backlog

See [roadmap.md](roadmap.md): distributed lock providers (SQL Server app locks), PostgreSQL provider sample,
admin/manual-trigger HTTP API, pause/resume, richer OpenTelemetry semantic conventions, dashboards,
NuGet publication workflow.
