# Roadmap

The Core is deliberately small: the reliability primitives, done properly, with honest
documentation. Everything below is a candidate for later versions, ordered by how much it
unblocks.

## Phase 2 — multi-instance safety

**Distributed lock providers.** The highest-value gap: today overlap protection is in-process, so
a second host instance can double-execute. `IJobLockProvider` already exists as the seam, and
since 2.0 the pending-occurrence queue already demonstrates the pattern (database-arbitrated
leases with expiry takeover and owner tokens) — for that one capability only.

- A SQL Server provider using application locks (`sp_getapplock`) or a lease table.
- A generic relational lease provider (lease row + expiry + fencing token) so PostgreSQL/MySQL
  work without provider-specific code.
- Startup recovery must become lease-aware: `Running` records must only be abandoned when their
  owner's lease has expired, replacing the current single-instance assumption
  (see [limitations.md](limitations.md)).

**Multi-instance execution store semantics.** Occurrence-identity duplicate suppression already
works across processes because it is a database check, but it is check-then-act; making it
atomic — and running a two-host test suite against one database — is what would let the engine
claim more than a single active instance.

## Phase 2 — operability

**Manual trigger HTTP API.** `IManualJobTrigger` exists; a small optional
`ResilientWorkerKit.AspNetCore` package could map `POST /worker-kit/jobs/{jobId}/trigger`,
`GET /worker-kit/jobs` and `GET /worker-kit/executions` with pluggable authorization.

**Pause and resume.** Runtime enable/disable per job, persisted, so operators can stop a
misbehaving job without a deployment. Requires a small amount of durable job state, which the
Core deliberately avoids.

**Dead-letter reprocessing.** `IDeadLetterStore.MarkReprocessedAsync` exists but nothing drives
it. A reprocessing helper (re-run a job scoped to a set of dead-lettered item ids) would close
the loop.

**History retention.** A built-in maintenance job that prunes execution records, expired
idempotency records and reprocessed dead letters on a configurable retention window.

## Phase 2 — persistence

- **PostgreSQL sample and CI coverage** (the EF Core model is already provider-agnostic).
- ~~SQL Server integration tests in CI~~ — done since 2.0: the store contract suite runs
  against a SQL Server service container on the Linux CI leg.
- **Bulk-friendly idempotency APIs** — batch `TryAcquire`/`MarkCompleted` to cut round trips for
  jobs processing thousands of items per page.

## Phase 3 — observability and tooling

- **Richer OpenTelemetry semantics**: align attribute names with OTel semantic conventions once
  the messaging/job conventions stabilize, plus a ready-made dashboard definition.
- **Durable health state** so "consecutive failures" survives a restart (today it is in-memory).
- **`ResilientWorkerKit.Testing` package**: `TestJobHost`, assertion helpers and a fake clock
  harness, extracted from this repository's own test infrastructure.
- **A read-only dashboard** — deliberately after the admin API, and deliberately optional.

## Phase 3 — execution model

- **Item-level retry helpers**: a `context.ForEachItemAsync(...)` that applies per-item retry,
  idempotency and dead-lettering with the ordering rules from
  [checkpoints.md](checkpoints.md) baked in.
- **Concurrency limits across jobs** (a global "at most N executions at once" budget).
- **Startup stagger/jitter** for hosts that run many jobs with identical intervals, to avoid
  synchronized load spikes after a redeploy.

## Explicit non-goals

These stay out regardless of demand, because they change what the library *is*:

- Distributed workflow orchestration, sagas, compensation (that is Temporal/Durable Task).
- A job queue with enqueue-from-anywhere semantics (that is Hangfire).
- Clustering with leader election as a core feature (Quartz territory).
- Storing job *definitions* in the database — for the **Core package**, schedules stay in code
  and in version control. Runtime-defined schedules are intentionally outside the Core package
  and are developed through optional Dynamic Scheduling packages in the same repository.

## NuGet publication

Publication is intentionally **not** wired into CI, so a push can never become an accidental
release. The remaining steps:

1. Reserve the `ResilientWorkerKit*` package ids on nuget.org.
2. Add a separate, manually triggered release workflow that builds, tests, packs and pushes with
   `dotnet nuget push` using a repository secret, gated on a tag.
3. Publish symbol packages (`.snupkg`) alongside, so SourceLink debugging works for consumers.
4. Keep the exactly-once limitation and the single-instance constraint at the top of the release
   notes — they are the two things an adopter most needs to know up front.

## Target frameworks

The libraries multi-target `net10.0` and `net8.0`, the two LTS releases in support. The `net8.0`
leg is dropped in the first major version released after .NET 8 leaves support in November 2026.

## Versioning policy

From 1.0 the library follows [semantic versioning](https://semver.org): breaking changes require a
major version, additive capabilities a minor one. Types under `*.Internal` namespaces and
`internal` types never carry a compatibility guarantee.
