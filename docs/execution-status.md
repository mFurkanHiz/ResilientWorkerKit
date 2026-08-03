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

- Post-implementation adversarial review (4 lenses + refutation): five confirmed findings —
  acquire-throw hot spin (blocking), queue-read failure stranding durable work, origin
  plan-write chain loss in-process, queued-overlap anchor regression (a pre-existing 1.x
  defect), inclusive-expiry off-by-one, stale observability catalogue — all fixed test-first
  and recorded as D-001/D-002 amendments in the decision log.

## In progress

- Awaiting the CI run for the review-fix commit; then the stage 1 exit report.

## Blockers

- None. SQL Server tests skip locally (no Docker on this machine) by design; the CI Linux leg
  executes them — verified: ubuntu ran 55/55 integration tests with zero skips.

## Last verified state

- Local (Release): unit 239 × 2 TFMs green; integration 45 green + 13 SQL-Server-skipped ×
  2 TFMs; `dotnet format` clean; 5 packages pack with package validation on.
- CI: first branch run fully green on both OSes, SQL Server leg included.

## Next step

Stage 1 exit report to the owner — then **stop at the stage gate**. Stage 2 (release
readiness) starts only on `DEVAM: SONRAKİ AŞAMAYA GEÇ`.
