# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): from 1.0 onwards, breaking changes
require a major version and additive capabilities a minor one.

## [1.1.1] — 2026-08-02

Fixes a defect that made `RetryLater` — the headline feature of 1.1.0 — not work in-process for
any job that performs I/O. **Anyone on 1.1.0 who uses `RetryLater` should upgrade.**

### Fixed

- **Follow-up retries never ran until the host restarted.** The scheduler loop decides what to
  wait for once per iteration and then sleeps, indefinitely when the schedule has no further
  occurrences. A follow-up is queued from inside the failing execution's own task — after that
  decision — and nothing woke the loop. The loop now waits on in-flight executions and on an
  explicit wake signal raised whenever durable work is enqueued.
- **A queued occurrence could be silently deleted.** Claiming a pending occurrence is a delete,
  and it ran before the duplicate and overlap checks. An occurrence that came due while the job
  was busy was claimed, then dropped with only a warning: no execution record, no dead letter,
  the durable row gone. The claim now happens last, after the decision to run, and a row is
  returned to the queue if the runner declines because the job lock was unavailable. Durable
  out-of-band work now waits for capacity instead of being discarded by the overlap policy,
  which governs schedule occurrences rather than planned actions.
- **Follow-up identities grew without bound.** Each token was chained onto the previous attempt
  (`…+followup-1+followup-2+followup-3`), which is unreadable and can overflow the
  300-character persisted column on providers that enforce lengths, such as SQL Server. Tokens
  are now derived from the origin occurrence and are bounded by construction.
- **A follow-up could shift the schedule anchor**, because "came from the schedule" was inferred
  from the presence of a claim, and the queued-behind-a-run path erased it. The distinction is
  now explicit, so an out-of-band retry cannot make a monthly job skip a month.

### Why the 1.1.0 tests did not catch it

They asserted the storage provider's behaviour rather than the engine's. The follow-up tests used
a job that threw synchronously, over a SQLite provider whose async API is implemented
synchronously, so an execution ran start-to-finish inside the call that started it and the loop
never had to notice the queued row. `JobScheduleLoopTests` had no coverage of the pending queue
at all. Coverage was 86% and reported nothing, because coverage measures which lines ran, not
whether they proved anything.

The test infrastructure now makes that an explicit axis: a yielding-store decorator forces every
store call to yield, and the loop-level tests use job bodies that await. Every fix above has a
test that fails without it.

## [1.1.0] — 2026-08-02

Planned actions: schedule something for a specific future moment, and make sure it eventually
happens even if the first attempt fails and the process is redeployed in between.

### Added

**Explicit-time schedules**
- `AtTimes(params DateTimeOffset[])` and `AtTimes(IEnumerable<DateTimeOffset>)` — fire at an
  explicit set of instants rather than a repeating pattern.
- `AtLocalTimes(timeZone, params DateTime[])` — the same, in wall-clock time, resolved with the
  existing daylight-saving rules.
- `Repeating(startAt, every, count)` — sugar for "three runs on the 15th, four hours apart".
- Each instant is its own occurrence with identity `at:<instant>`, so a completed one is never
  repeated after a restart. Default misfire policy is `RunImmediatelyOnce`.

**Durable follow-up retries**
- `RetryLater(maxAttempts, delay)` and `RetryLater(Action<FollowUpRetryOptions>)` queue a new
  execution after one has failed for good. Unlike `WithRetry`, whose attempts live in memory
  inside a single execution, a follow-up is persisted and runs even if the process that queued it
  is gone. Supports even spacing or exponential backoff with a ceiling.
- Permanent and misconfigured failures do not queue a follow-up unless
  `RetryPermanentFailures` is set.
- New store interface `IPendingOccurrenceStore` with in-memory and EF Core implementations, and a
  new table `WorkerKitPendingOccurrences`. Claiming an occurrence is a delete, so the database
  picks the single winner. The record shape is deliberately general (`Source`, `PayloadJson`) so
  runtime-created triggers can reuse it later without a schema change.
- New metric `workerkit.job.follow_ups` and log events 1031–1034.

### Fixed

- A planned schedule whose instant had already passed was never discovered on a first start
  against an empty store: the engine only scheduled forward from "now", so a host that was down
  at the planned minute skipped the action silently instead of letting the misfire policy decide.
  `IJobSchedule` gained `DiscoverPastOccurrencesOnFirstStart` (a default interface member, so
  existing implementations are unaffected); one-time and explicit-time schedules return true,
  recurring schedules keep looking forward only.

### Upgrading

EF Core users need a migration for the new `WorkerKitPendingOccurrences` table; see
[docs/persistence.md](docs/persistence.md). Everything else is additive and source-compatible.

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

**Registration**
- `AddResilientWorkerKit` is safe to call more than once on one service collection: calls share
  one options instance and contribute to one job registry.
- The engine's hosted service always starts last, so store initializers registered from inside
  the callback are guaranteed to run first.

**Quality**
- 192 tests (179 unit, 13 integration), executed against **both** target frameworks — 384 test
  executions per CI leg. Integration tests use a real Generic Host, a real SQLite file that
  survives restarts and a real HTTP server, including the full failure → restart → resume scenario.
- Measured coverage: 85.4% lines, 78.3% branches, 93.9% methods.
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

[1.1.1]: https://github.com/mFurkanHiz/ResilientWorkerKit/releases/tag/v1.1.1
[1.1.0]: https://github.com/mFurkanHiz/ResilientWorkerKit/releases/tag/v1.1.0
[1.0.0]: https://github.com/mFurkanHiz/ResilientWorkerKit/releases/tag/v1.0.0
