# Master plan

This document is the long-term execution plan for the ResilientWorkerKit repository. It was
agreed at the end of the read-only audit (stage 0) and is binding for all later stages. A new
working session starts by reading this file, [execution-status.md](execution-status.md) and
[decision-log.md](decision-log.md), in that order.

## Go / No-Go state

| Work | State |
|---|---|
| Core hardening (stage 1) | **GO** — approved |
| NuGet publication of anything | **NO-GO** until stages 1 and 2 are complete **and** the owner explicitly approves publication |
| Dynamic Scheduling implementation | **NO-GO** until the stage 3 design is explicitly approved |

Approval is given per gate, in chat, by the repository owner. No gate approval is ever implied
by another gate.

## Product decisions already made

1. **One product line.** v1.0.0 → v1.1.0 → v1.1.1 are versions of the same Core product. There
   is no rollback to v1.0.0; v1.1.1 is the starting point.
2. **Planned-time features stay in Core.** `OnceAt`, `AtTimes`, `AtLocalTimes`, `Repeating`,
   `RetryLater`, `ExplicitTimesSchedule` and pending-occurrence persistence remain in the Core
   packages. They are audited and hardened, not removed.
3. **Dynamic Scheduling is separate packages in this repository** (monorepo, no second repo for
   now): `ResilientWorkerKit.DynamicScheduling.Abstractions`, `.DynamicScheduling`,
   `.DynamicScheduling.EntityFrameworkCore`, `.DynamicScheduling.AspNetCore`. The count may be
   simplified during stage 3 design, but Dynamic Scheduling never moves into the Core packages.
4. **Dependency direction:** Dynamic Scheduling → Core. Core never references a Dynamic
   Scheduling package.
5. **Application-specific handlers** (ticket-sale openers, payment clients, campaign senders and
   the like) live in samples or consuming applications, never in the generic packages.
6. **Execution model:** at-least-once + durable persistence + leases + deterministic occurrence
   identity + idempotent handlers. Exactly-once is never claimed.
7. **NuGet strategy:** the audited Core family publishes first. v1.0.0 is not re-published.
   **v1.1.0 is deliberately never published** — its `RetryLater` did not run in-process for jobs
   that await (see CHANGELOG 1.1.1). Dynamic Scheduling starts at `0.1.0-preview.1` later.
8. **Versioning:** SemVer. The Core version for the next release is decided by the outcome of
   the stage 1 lease design (recorded in [decision-log.md](decision-log.md)), not chosen in
   advance to avoid or force a major bump.

## Stages and gates

### Stage 0 — read-only audit *(complete)*

Twenty-section audit of Core v1.1.1; no files changed. Accepted with binding corrections, which
are folded into the stage descriptions below. Key confirmed findings: the pending-occurrence
claim-as-delete crash window; the silent end of a `RetryLater` chain when the process crashes
mid-execution; silent-loss edge cases in explicit-time identity precision; unvalidated
`Repeating` ranges; the untested SQL Server compatibility claim; stale v0.1 documentation.

### Stage 1 — Core hardening *(current)*

Scope, in priority order:

1. **Pending-occurrence crash safety.** Replace claim-as-delete with a lease model. The store
   contract decision (options A–D) is analysed and recorded in
   [decision-log.md](decision-log.md) before implementation. Two distinct blocking problems:
   - **A — crash after claim, before the execution record exists.** The occurrence must not be
     lost; an expired lease must make it acquirable again.
   - **B — crash after execution started.** `Running` records may be marked `Abandoned` at
     startup, but how a `RetryLater` chain continues is designed separately, together with
     idempotency, origin occurrence, execution attempt, follow-up ordinal, retry policy, safe
     recovery policy and duplicate-side-effect risk. **No automatic retry for every `Abandoned`
     execution** — an external call may have succeeded without its response being observed.
2. **Follow-up retry correctness** on top of the lease model (release on lock decline, complete
   after the terminal record, no lost rows).
3. **Multi-instance readiness — scoped.** The lease work makes *the pending-occurrence
   capability* multi-instance safe: single lease owner per occurrence, expiry recovery,
   owner-token-checked complete/release. **This does not make ResilientWorkerKit multi-instance
   safe as a whole.** Full multi-instance support is not claimed until distributed job locks,
   lease-aware startup recovery, atomic scheduled-occurrence dedup, cross-process wake/polling
   and protection of live hosts' executions from `Abandoned` marking are all solved. Stage 1
   tests and documentation state this boundary explicitly.
4. **Explicit-times validation.** Fail fast (with a clear `JobConfigurationException`) both on
   exact duplicate timestamps and on distinct timestamps that collide at the second-precision
   identity. The identity format itself does not change, because changing it could re-run
   completed occurrences after an upgrade. Behaviour changes are called out in the CHANGELOG.
5. **Repeating safety.** The chosen approach (eager cap vs configurable limit vs lazy finite
   schedule) is justified in the decision log. Whatever is chosen: OOM-capable counts fail
   fast, multiplication and `DateTimeOffset` range overflows are caught, and configuration
   errors surface as `JobConfigurationException`, never as raw overflow exceptions.
6. **Real-async I/O, restart and crash tests** for every fix above.
7. **SQL Server.** Integration tests (CI service container on the Linux leg; env-var-gated and
   skipped where no server is available) keep the compatibility claim honest. If the tests
   cannot be added, the claim is softened everywhere to "SQLite verified; SQL Server
   designed/expected but not integration-tested" instead.
8. **Package validation** enabled for the library projects.
9. **Documentation consistency.** Stale v0.1/MVP references fixed; `PendingOccurrence.PayloadJson`
   documented as write-only/reserved (it is not a runtime payload feature); the 1.1.0
   `RetryLater` defect stays in the history; no hand-maintained test counts; no signing claims.

Exit: report changed files, architecture decisions, SemVer proposal, migration impact, full
test results on net8.0 and net10.0, Linux and Windows CI state, coverage, remaining risks —
then **stop**. Continue only on `DEVAM: SONRAKİ AŞAMAYA GEÇ`.

### Stage 2 — Core NuGet release readiness *(gate: DEVAM)*

No publishing in this stage. Prepare: NuGet-specific short README per package, package icon,
metadata review, SourceLink + snupkg verification, package validation, dependency gate,
deterministic build check, clean-consumer restore test, manual tag-gated release workflow,
Trusted Publishing/OIDC preparation with scoped-API-key fallback documentation, release
checklist, rollback plan, package ownership plan. Fix remaining version contradictions in docs;
[roadmap.md](roadmap.md) must not say v0.1. Core keeps "runtime DB-defined job definitions" as
a non-goal with the clarification: *"Runtime-defined schedules are intentionally outside the
Core package and are developed through optional Dynamic Scheduling packages in the same
repository."* Exit: release-candidate report, then **stop**.

### Stage 2B — Core NuGet publication *(gate: NUGET YAYININI ONAYLIYORUM)*

Clean-checkout test → restore → build → test → package validation → pack → local-feed consumer
test → tag check → manual release workflow → publish → symbol verification → nuget.org page
check → clean-project restore from nuget.org → README install instructions. A package that is
not right is never force-published; publication is cancelled and the reason reported.

### Stage 3 — Dynamic Scheduling design *(gate: DEVAM)*

Design only, no implementation. Produces the eleven documents under `docs/dynamic-scheduling/`
(architecture, domain model, event lifecycle, occurrence planning, lease model, handler
registry, payload security, API contract, testing strategy, package plan, roadmap). Core
remains unchanged. Runtime action security: `ActionType` resolves only to allowlisted,
DI-registered handlers; never arbitrary types, assemblies, scripts, shell commands or
uncontrolled URLs. Payloads carry a schema version, are validated, size-limited, never contain
secrets, and PII is forbidden by default. Occurrence recurrence and execution retry are never
conflated. Exit: documents ready, then **stop**.

### Stage 4 — Dynamic Scheduling preview implementation *(gate: DEVAM)*

First preview scope and exclusions as agreed in the stage 3 documents; fictional sample domain
only (`samples/ScheduledActions.*`); the full test list from the plan. Exit: report, **stop**.

### Stage 5 — Dynamic preview package readiness *(gate: PREVIEW PAKET YAYININI ONAYLIYORUM)*

Preview packages at `0.1.0-preview.1`. The Core version is not bumped because of the preview.
Publication only on explicit approval.

## Control commands

`ONAY: AŞAMA 1'E GEÇ` · `DEVAM: SONRAKİ AŞAMAYA GEÇ` · `DURUM RAPORU` · `DUR` ·
`BU AŞAMAYI GERİ AL` · `NUGET YAYININI ONAYLIYORUM` · `PREVIEW PAKET YAYININI ONAYLIYORUM`

`DEVAM` always means "the next approved stage in this plan", nothing else.

## Standing quality rules

Nullable enabled; warnings as errors; `TimeProvider` everywhere; `CancellationToken`
everywhere; structured logging with constant templates; no `Thread.Sleep`, fire-and-forget,
`Environment.Exit`, swallowed exceptions or secret/body logging; deterministic occurrence
identities; atomic concurrency operations; real async test paths; Linux and Windows CI on
net8.0 and net10.0; package validation, SourceLink and symbol packages for anything shipped.
Coverage percentages are never presented as a quality guarantee, and test counts are never
hand-maintained in documentation.
