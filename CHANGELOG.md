# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): from 1.0 onwards, breaking changes
require a major version and additive capabilities a minor one.

## [1.0.0] — 2026-08-01

First public release. The public API is fixed under semantic versioning; the reliability
primitives are complete for the stated scope, and the documentation matches the code.

Targets **`net10.0` and `net8.0`** — the two LTS releases in support — with framework-matched
dependencies for each.

### Added

**Execution engine**
- `IWorkerJob` with `JobExecutionContext`: scoped DI per execution, job-scoped logger, typed
  checkpoints, idempotency, item-level dead letters and safe progress reporting.
- Failure isolation at two boundaries — a job exception can reach neither the scheduler loop nor
  the host.
- Retry with failure classification (transient / permanent / cancelled / timed-out / abandoned /
  misconfigured), exponential backoff with jitter, `Retry-After` support, attempt and total
  timeouts. `ExecutionId` is stable across attempts.
- Overlap policies: `SkipNewExecution` (default), `QueueSingleExecution`, `AllowConcurrentExecutions`.
- Graceful shutdown with a configurable grace period; cancellation is a distinct, non-error outcome.
- Startup recovery: executions left `Running` by a dead process are marked `Abandoned`.
- `IManualJobTrigger` extension point.

**Scheduling**
- Interval, fixed-delay, cron (Cronos, 5- and 6-field), daily, weekly, monthly, last-day-of-month,
  one-time, run-on-startup, and custom `IJobSchedule`.
- Per-job IANA time zones with explicit DST handling: spring-forward gaps shift to the end of the
  gap, the fall-back hour fires exactly once.
- Monthly invalid-day policies: `SkipMonth`, `RunOnLastAvailableDay`, `FailConfiguration`.
- Misfire policies: `Skip`, `RunImmediatelyOnce`, `RunIfWithinTolerance`, `RescheduleFromNow`,
  with per-schedule-type defaults and restart-safe recovery.
- Deterministic occurrence identity (`monthly-billing:2026-08`) preventing duplicate runs across
  restarts.

**Persistence**
- In-memory stores for tests and demos, documented as unsuitable for production.
- EF Core stores (SQLite verified, SQL Server compatible) for execution history, checkpoints,
  idempotency records and dead letters. The idempotency race is settled by a composite primary
  key plus a concurrency token.

**HTTP integration**
- `AddResilientApiClient` over `IHttpClientFactory` and `Microsoft.Extensions.Http.Resilience`.
- Correlation-ID and Idempotency-Key propagation (the key handler sits outside the retry handler,
  so retries reuse one key), API-key and bearer handlers with token caching and refresh-on-401.
- `EnsureApiSuccessAsync` producing safe errors that never contain query strings or bodies,
  `ApiRequestException` feeding the engine's retry classification, pagination helpers.

**Observability and health**
- Structured logging with correlation scopes and constant message templates.
- Metrics via `System.Diagnostics.Metrics` and tracing via `ActivitySource` — no adapter package
  required.
- Per-job health checks with configurable thresholds and stuck detection.

**Quality**
- 182 tests (169 unit, 13 integration). Integration tests use a real Generic Host, a real SQLite
  file that survives restarts and a real HTTP server, including the full
  failure → restart → resume scenario.
- CI on Linux and Windows: warnings as errors, format verification, coverage, vulnerable-dependency
  gate, sample builds and packaging.
- 18 documents covering architecture, semantics, every subsystem, security, testing, limitations
  and roadmap.

### Known limitations

- No exactly-once execution — the contract is at-least-once plus checkpoints plus idempotency.
- Single active host instance: locking is in-process; distributed locking is the top roadmap item.
- No dashboard or admin API; job definitions live in code.
- Packages are not published to NuGet yet; the release assets are the CI-equivalent builds.

These are boundaries of the 1.0 scope, not defects. Lifting the single-instance constraint is
additive (`IJobLockProvider` is already the seam) and is planned for a 1.x release.

See [docs/limitations.md](docs/limitations.md) for the full list.

[1.0.0]: https://github.com/mFurkanHiz/ResilientWorkerKit/releases/tag/v1.0.0
