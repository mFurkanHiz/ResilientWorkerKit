# Execution status

Updated at the end of every stage (and at significant mid-stage checkpoints). A new working
session reads [master-plan.md](master-plan.md), this file, and
[decision-log.md](decision-log.md) before doing anything else.

## Current stage

**Stage 1 — Core hardening** (branch `core-hardening-2.x`): implementation complete, in final
verification. The stage gate report goes to the owner next; stage 2 starts only on
`DEVAM: SONRAKİ AŞAMAYA GEÇ`.

## Completed

- Governance documents; decisions D-001…D-007 recorded, all now **accepted** (D-001 lease
  contract option A; D-002 crash semantics incl. the review amendments; D-003 unique-index
  identity arbitration; D-004 explicit-times fail-fast; D-005 lazy `RepeatingSchedule`;
  D-006 SQL Server proven in CI; D-007 version 2.0.0).
- Pre-implementation adversarial design review (4 attack lenses + refutation pass): one
  blocking finding (complete-after-failed-planning loses the chain) and one serious finding
  (decision-log/self-contradiction) — both folded into the design before code was written.
- Lease model implemented end to end: contract, in-memory + EF Core stores, engine
  integration (acquire → heartbeat → outcome-gated complete / release), decline cooldown,
  cancelled-run release, stale-row cleanup, `ContinueAfterAbandoned` opt-in recovery.
- Explicit-times validation, lazy `RepeatingSchedule`, builder/misfire wiring.
- Tests: store lease contract suite × {in-memory, SQLite, SQL Server (env-gated)}, engine
  lease suite (11 scenarios incl. crash re-delivery, heartbeat, planning-failure retention),
  crashed-process SQLite integration test; all pre-existing suites green on both TFMs.
- CI: SQL Server service container on the Linux leg; `EnablePackageValidation`; version
  2.0.0; documentation consistency pass (stale v0.1 references, claim-as-delete wording,
  multi-instance boundary, migration SQL with dedup step, PayloadJson write-only note);
  CHANGELOG 2.0.0 entry.

## In progress

- Final full verification (build, format, both TFMs, pack) and the stage 1 exit report.

## Blockers

- None. SQL Server tests skip locally (no Docker on this machine) by design; the CI Linux leg
  executes them.

## Last verified state

- Local: unit 235 × 2 TFMs green; integration 43 green + 12 SQL-Server-skipped × 2 TFMs;
  `dotnet format` clean. CI run on this branch: pending push.

## Next step

Push the branch, run CI (workflow_dispatch), adversarial code review of the full diff, then
the stage 1 exit report — and **stop at the stage gate**.
