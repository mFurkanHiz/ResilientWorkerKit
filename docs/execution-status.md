# Execution status

Updated at the end of every stage (and at significant mid-stage checkpoints). A new working
session reads [master-plan.md](master-plan.md), this file, and
[decision-log.md](decision-log.md) before doing anything else.

## Current stage

**Release preparation for 2.0.0 — the publication gate is still closed.**
Stage 2 (release readiness) is complete and **merged to `main`**: squash commit
`4d69add2e09bba50c424046663350ac098e2c9a6`, main CI run `30831214511` fully green (both
OSes, both TFMs, Linux SQL Server leg zero skips, format, vulnerability gate, samples,
package validation, pack). This release-prep pass dates the CHANGELOG, fixes the 2.0.0
compare link to the future tag, and corrects the release workflow's ref model (dispatch from
`main` only; the `nuget-release` environment restricted to `main`; the tag input selects
what is published, verified to lie on main's history).

**No `v2.0.0` tag exists, no GitHub release was created, nothing has been published to
NuGet, and no package ID has been reserved.** Stage 2B (actual publication) starts only on
the owner's explicit `NUGET YAYININI ONAYLIYORUM`.

## What is ready

- Five packages at 2.0.0 with per-package NuGet READMEs, icon, validated metadata,
  SourceLink-verified symbol packages, and clean-consumer smoke tests on net10.0 and net8.0.
- The Release workflow: manual-only, main-ref-guarded, tag-gated (version match + ancestry
  on main), environment-gated, full test suite (SQL Server included) against the exact tag,
  Trusted Publishing first with a scoped-API-key fallback, idempotent pushes.
- [release-checklist.md](release-checklist.md): exact Trusted Publishing policy values, the
  `nuget-release` environment settings (deployment branches = `main`, required reviewer,
  prevent-self-review off for a sole maintainer), the 2B publish sequence, ownership plan,
  and the rollback/unlist plan.
- All five `ResilientWorkerKit*` package ids verified available on nuget.org (not reserved).

## Owner prerequisites before 2B

1. Credentials: Trusted Publishing policy on nuget.org (repository owner `mFurkanHiz`,
   repository `ResilientWorkerKit`, workflow `release.yml`, environment `nuget-release`) plus
   the `NUGET_USER` secret and `NUGET_TRUSTED_PUBLISHING=true` variable — or the scoped
   `NUGET_API_KEY` secret as fallback.
2. The `nuget-release` environment created in repository settings per the checklist.

## Next step

**Waiting on the owner:** merge decision for the release-prep PR, the prerequisites above,
and `NUGET YAYININI ONAYLIYORUM` for stage 2B (tag `v2.0.0` on main → dispatch Release from
main → verify nuget.org → README flip → GitHub release). Until then: no tag, no release, no
publish, no stage-3 work.
