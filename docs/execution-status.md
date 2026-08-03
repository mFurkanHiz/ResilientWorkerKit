# Execution status

Updated at the end of every stage (and at significant mid-stage checkpoints). A new working
session reads [master-plan.md](master-plan.md), this file, and
[decision-log.md](decision-log.md) before doing anything else.

## Current stage

**Stage 2 — Core NuGet release readiness: complete; awaiting the publication gate.**
Stage 1 was merged to `main` via PR #1 (squash commit `594d913`, main CI green). Stage 2 ran
on branch `release-readiness-2.0`. **Nothing is published, no tag exists, no GitHub release
was created, no package ID was reserved.** Stage 2B (actual publication) starts only on the
owner's explicit `NUGET YAYININI ONAYLIYORUM`.

## What stage 2 delivered

- **Per-package NuGet READMEs** (5 short pages written for nuget.org: absolute links, honest
  scope, quick starts verified against the real APIs) replacing the long repository README in
  the packages; **package icon** (`assets/icon.png`) wired into all five.
- **Metadata:** `PackageReleaseNotes` → CHANGELOG; existing descriptions/tags/license/
  SourceLink audit passed; `EnablePackageValidation` remains on.
- **SourceLink + symbols verified:** `sourcelink test` passed on all 10 PDBs (5 packages ×
  2 TFMs) extracted from the `.snupkg` files of a CI-equivalent pack.
- **Clean consumer smoke test:** a fresh console project restored **only** from the local
  `artifacts/` feed (+nuget.org for framework deps), built and ran a real job on **net10.0
  and net8.0** — the net8.0 consumer resolved the 8.0.x dependency line, proving the
  framework-matched dependency claim at package level.
- **Dependency audit:** zero vulnerable (incl. transitive); only deprecated item is the
  test-only `xunit 2.9.3` (Legacy; migration to xunit.v3 is a future chore, ships in no
  package); "outdated" hits are the net8.0 group seeing 10.x — by design.
- **Release workflow** ([release.yml](../.github/workflows/release.yml)): manual-only,
  tag-gated, `nuget-release` environment-gated, full tests (SQL Server service container)
  against the exact tag, Trusted Publishing (OIDC) preferred with scoped-API-key fallback,
  `--skip-duplicate` idempotent pushes. No push-based trigger exists.
- **[release-checklist.md](release-checklist.md):** operator checklist, credential setup,
  ownership plan, rollback/unlist plan, and the exact stage-2B publish sequence.
- **Package ID availability:** all five `ResilientWorkerKit*` ids return 404 on nuget.org —
  available; deliberately not reserved (needs owner approval).
- [roadmap.md](roadmap.md) publication section updated to the real state.

## Verified state

- Branch `release-readiness-2.0`; CI on its head: see the stage-2 closure report (dispatched
  run on the exact head).
- Local (Release, CI=true): full build zero warnings; unit 251 × 2 TFMs and integration
  47 + 14 SQL-skips × 2 TFMs green; `dotnet format` clean; 10 artifacts packed with
  validation; consumer smoke tests green on both TFMs.

## Open risks

1. Trusted Publishing requires one-time setup on nuget.org by the account owner (policy +
   `NUGET_USER` secret + `NUGET_TRUSTED_PUBLISHING` variable) — or the scoped-key fallback;
   neither can be prepared by this repository alone.
2. The `nuget-release` environment exists only once created in repository settings; adding
   required reviewers there is recommended before 2B.
3. nuget.org README/icon rendering can only be finally confirmed after the first publish
   (checklist step 4 covers it).
4. Multi-instance limits and the origin-chain boundary are unchanged from stage 1 —
   documented, not blocking publication.

## Next step

**Waiting on the owner:** `NUGET YAYININI ONAYLIYORUM` for stage 2B (tag `v2.0.0`, dispatch
the Release workflow, verify nuget.org, flip the README install section, GitHub release).
Until then: no tag, no release, no publish, no Dynamic Scheduling work.
