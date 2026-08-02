# Execution status

Updated at the end of every stage (and at significant mid-stage checkpoints). A new working
session reads [master-plan.md](master-plan.md), this file, and
[decision-log.md](decision-log.md) before doing anything else.

## Current stage

**Stage 1 — Core hardening** (branch `core-hardening-2.x`), approved on 2026-08-02 with
binding corrections; see the stage 1 section of the master plan.

## Completed

- Stage 0 read-only audit: presented and accepted (2026-08-02).
- Governance documents created (this commit).
- Decisions D-001 … D-007 analysed and recorded in the decision log; D-001 (lease contract,
  option A) and D-002 (crash-during-execution semantics) are the load-bearing ones.

## In progress

- Lease-model implementation: store contract reshape, in-memory + EF Core stores, engine
  integration (acquire → run → renew → complete/release), expired-lease re-delivery.
- Explicit-times duplicate/collision validation; `Repeating` lazy schedule.
- Test suites: lease lifecycle, two-store single-winner, expiry recovery, owner-token checks,
  crash/restart integration, SQL Server (env-var-gated).

## Blockers

- None. (SQL Server tests cannot run on the local machine — no Docker; they are executed by
  the CI Linux leg's service container and skip locally by design.)

## Last verified state

- Base: v1.1.1 (`212f852`), CI green on ubuntu/windows × net8.0/net10.0.
- This branch: not yet pushed; test state recorded here after each local full run.

## Next step

Implement D-001/D-002/D-004/D-005 test-first; then documentation consistency (stale v0.1
references, PayloadJson wording, multi-instance boundary), package validation, CI SQL Server
service; full local verification; push branch and run CI via workflow_dispatch; stage 1 exit
report; **stop for the stage gate**.
