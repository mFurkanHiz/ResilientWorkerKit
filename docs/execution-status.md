# Execution status

Updated at the end of every stage (and at significant mid-stage checkpoints). A new working
session reads [master-plan.md](master-plan.md), this file, and
[decision-log.md](decision-log.md) before doing anything else.

## Current stage

**Stage 1 — Core hardening: implementation complete.** Branch `core-hardening-2.x`; a pull
request to `main` carries the full diff. The stage is closed for code; what remains is the
owner's decision at the gate: merge approval and `DEVAM: SONRAKİ AŞAMAYA GEÇ` for stage 2
(release readiness). **Nothing has been published to NuGet and no tag exists for 2.0.0.**

## What stage 1 delivered

- **Lease-based pending-occurrence safety** (D-001, option A — breaking): acquire → heartbeat
  renewal at duration ∕ 3 → outcome-gated complete / release; visibility-based expiry
  recovery; unique `(JobId, IdentityToken)` index as the cross-process "already queued"
  arbiter; version set to **2.0.0** (D-007).
- **Crash semantics** (D-002): re-delivery at the same ordinal for pending-sourced runs;
  `Cancelled` never completes a row; opt-in `ContinueAfterAbandoned` for origin crashes;
  origin plan-write failures retried in-process with backoff.
- **Validation**: explicit-times duplicate/same-second fail-fast (D-004); lazy
  `RepeatingSchedule` with range checks (D-005); global `WorkerKitOptions` fail-fast
  validation (lease duration, host id presence and column-limit length, non-negative
  timeouts) run before any loop is constructed.
- **Two adversarial review rounds**, pre- and post-implementation; all confirmed findings
  fixed test-first and recorded as amendments in the decision log (including a pre-existing
  1.x anchor-regression defect).
- **SQL Server proven in CI** (D-006): the store contract suite runs against a service
  container on the Linux leg — zero skips there; skips on Windows and locally are explicit.
- `EnablePackageValidation` on all five packages; documentation consistency pass (no v0.1 or
  claim-as-delete remnants; observability catalogue complete through event 1042; migration
  SQL with the required dedup step in [persistence.md](persistence.md)).

## Verified state (final for this stage)

- **Last code commit:** `26ae6f6` (options validation); this status update lands as the
  commit after it, which is the PR head. The binding CI evidence for the stage is the check
  run **on that PR head** — recorded in the PR's checks and in the stage-closure report.
- **Local (Release, both TFMs):** unit **251/251**; integration **47 passed + 14 SQL Server
  skips** (no Docker locally, by design); `dotnet format` clean; zero build warnings; all 5
  packages pack with package validation enabled.
- **CI expectation on the PR head** (matched by the previous head's run, id 30802298893, all
  jobs green): ubuntu — unit 251 × 2 TFMs, integration **61/61 with zero skips** (SQL Server
  service container); windows — unit 251 × 2 TFMs, integration 47 + 14 explicit skips;
  format, vulnerable-dependency gate, sample builds and pack all green. Coverage (Linux leg):
  ~86% line / ~79% branch / ~94% method — a measurement, not a quality guarantee.

## Open risks (deliberate, documented)

1. Multi-instance boundary unchanged: only the pending-occurrence capability is lease-proven
   for multiple hosts; job locks, startup recovery, schedule dedup and cross-process wake
   remain single-instance ([limitations.md](limitations.md)).
2. An origin chain is lost only if the process dies before its follow-up write ever lands
   *and* `ContinueAfterAbandoned` is off — stated in [failure-handling.md](failure-handling.md).
3. Cross-host clock skew must stay under lease ∕ 3 (~100 s at the default) — stated in
   [persistence.md](persistence.md).
4. Package-baseline API validation becomes possible only after the first NuGet publication
   (stage 2 concern).

## Next step

**Waiting on the owner:** merge decision for the PR and stage-2 approval
(`DEVAM: SONRAKİ AŞAMAYA GEÇ`). No merge, no publication and no stage-2 work happens before
that.
