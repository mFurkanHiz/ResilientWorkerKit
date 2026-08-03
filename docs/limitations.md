# Limitations

Read this before adopting the library. Every item is a deliberate boundary of the current
scope, not an oversight, and each one is stated here rather than discovered in production.

## No exactly-once execution

The contract is **at-least-once execution + durable checkpoints + idempotent processing**. A job
body can run more than once over the same data after a crash, a retry, or a host restart.

Why: there is no distributed transaction spanning an external API call and your local database.
Any system claiming exactly-once across that boundary is either enrolling both sides in a
protocol you would notice, or misrepresenting at-least-once.

What to do: derive a stable idempotency key per side effect and let the kit's idempotency store
suppress duplicates. See [execution-semantics.md](execution-semantics.md) and
[idempotency.md](idempotency.md).

## Single active host instance

Overlap protection is **in-process** (`InProcessJobLockProvider`). Two host instances pointed at
the same database will both schedule and both execute — including destructive work.

Startup recovery assumes the same thing: any execution still in `Running` state at startup is
attributed to a dead process and marked `Abandoned`. With two live instances this misclassifies
the other instance's healthy execution. If you must run multiple instances today, set
`WorkerKitOptions.RunStartupRecovery = false` and understand that you have no overlap protection
between them.

**One capability is an exception, and only one.** The pending-occurrence queue (follow-up
retries) uses database-arbitrated leases: a single lease winner per occurrence, expiry-based
takeover from dead hosts, and owner-token-checked completion, proven by a store contract suite
that runs against SQLite and SQL Server. That makes *that queue* correct under multiple hosts —
it does **not** make the library multi-instance safe. Scheduled occurrences can still
double-run (the completed-identity check is check-then-act across processes), job locks are
still per-process, startup recovery still abandons other hosts' live executions, and a host
never wakes another host's scheduler. Full multi-instance support requires all of: a
distributed `IJobLockProvider`, lease-aware startup recovery, atomic scheduled-occurrence
dedup, and cross-process wake or polling — until every one of those exists, run one active
instance.

`IJobLockProvider` exists precisely so a distributed implementation can be dropped in; it is the
top item in [roadmap.md](roadmap.md).

## No dashboard, no admin API

There is no UI, no HTTP surface to list executions, and no built-in endpoint to trigger a job.
`IManualJobTrigger` gives you the mechanism; the endpoint around it is yours to write, with your
own authorization.

## Job definitions live in code

Jobs are registered at startup through `AddResilientWorkerKit`. There is no runtime registration,
no editing a schedule in a database, and no pause/resume switch. Changing a schedule means a
deployment. (`Disabled()` exists, but it is also a code change.)

## Not a workflow engine

No job dependencies, no fan-out/fan-in, no compensation, no sub-workflows, no durable
continuations. If job B must run after job A produced something, model that in your data (B looks
for work A left behind) rather than expecting orchestration.

## Scheduling boundaries

- **Skew and precision.** Occurrences fire when a `TimeProvider` delay elapses; expect
  sub-second-to-second precision under load, not millisecond determinism.
- **Long delays are chunked** (20-day segments) so far-future occurrences survive clock changes,
  but a machine suspended across an occurrence relies on the misfire policy, not on the timer.
- **Misfire recovery collapses runs.** If ten occurrences were missed, `RunImmediatelyOnce` runs
  the most recent one — not all ten.
- **`RescheduleFromNow` is rejected for calendar schedules** at startup, because re-anchoring "the
  5th of the month" to an arbitrary instant is meaningless.

## Time zone data

Time zone resolution uses `TimeZoneInfo.FindSystemTimeZoneById`, which needs IANA data present on
the host. On .NET 8 and .NET 10 this works on Linux and on Windows (via ICU), but a container built
`InvariantGlobalization=true` or without tzdata will throw `JobConfigurationException` at
startup — deliberately, at startup, rather than silently running in UTC.

## Persistence

- Only in-memory and EF Core stores ship. In-memory is **not** production-suitable (state dies
  with the process); this is stated in the XML docs of every in-memory type.
- The EF Core model is provider-agnostic and tested on SQLite; SQL Server compatibility is by
  design (`nvarchar`/`datetime2` friendly types, no provider-specific SQL) but is not covered by
  an automated test in this repository.
- Timestamps persist as UTC `DateTime`, not `DateTimeOffset`, because SQLite cannot `ORDER BY` or
  compare `DateTimeOffset`. The public abstractions still expose `DateTimeOffset`.
- **Nothing prunes history.** Execution records, idempotency records and dead letters accumulate
  until you delete them.

## HTTP integration

- The package targets JSON/REST-shaped APIs. No SOAP, no gRPC helpers.
- Two retry layers exist (the HTTP pipeline inside one attempt, the engine across attempts). This
  is powerful but easy to misconfigure into 3 × 3 = 9 real calls; see
  [api-integration.md](api-integration.md).
- `CachingBearerTokenProvider` caches per process, not across instances.

## Observability

- Metrics and traces use BCL primitives; there is no OpenTelemetry adapter package, so you wire
  the meter and activity source yourself (one line each).
- Health state is **in-memory**: after a restart, "consecutive failures" starts from zero even
  though the durable history shows the failures.

## Testing your own jobs

The kit is testable (`TimeProvider` everywhere, interfaces for every store), but this repository
ships no `ResilientWorkerKit.Testing` package. See [testing.md](testing.md) for the patterns to
copy.

## Version status

v1.0.0 fixes the public API under [semantic versioning](https://semver.org): breaking changes
require a major version, new capabilities a minor one. Types under `*.Internal` namespaces and
`internal` types carry no compatibility guarantee at any version.

The `net8.0` target will be dropped in a future major version once .NET 8 leaves support
(November 2026).
