# Release checklist

The operator's guide for publishing the ResilientWorkerKit packages. The mechanics live in
[.github/workflows/release.yml](../.github/workflows/release.yml) — manual-only, tag-gated,
environment-gated; no git operation can publish by accident. This document is the judgment
part: what to verify before a tag exists, how to publish, and what to do when a published
package turns out to be wrong.

## Security model, in one paragraph

Publishing requires **all** of: a human dispatching the Release workflow by hand, an existing
tag whose version matches the build, the `nuget-release` environment's protection rules (add
required reviewers there for a second approval), and a credential that only the workflow can
reach. CI never publishes; pushes never publish; the release workflow re-runs the full test
suite (SQL Server included) against the exact tag before packing what it pushes.

### Credentials — Trusted Publishing first

1. **Trusted Publishing (preferred, no long-lived secret).** On nuget.org, signed in as the
   package-owner **user account**, create the policy with exactly these values:

   | Field | Value |
   |---|---|
   | Repository owner | `mFurkanHiz` |
   | Repository | `ResilientWorkerKit` |
   | Workflow file | `release.yml` |
   | Environment | `nuget-release` |

   Then on GitHub (repository settings): **variable** `NUGET_TRUSTED_PUBLISHING` = `true`,
   and **secret** `NUGET_USER` = the nuget.org **profile username** (the display name on
   your nuget.org profile — **not** an e-mail address). The workflow exchanges a GitHub OIDC
   token for a short-lived push key at run time. Never write a token value anywhere in the
   repository, and never share one in a conversation.
2. **Scoped API key (fallback):** nuget.org → *API keys* → create a key scoped to **Push
   only**, glob `ResilientWorkerKit*`, shortest practical expiry (90 days or less). Store it
   as the `NUGET_API_KEY` secret. Rotate on every expiry; delete it once Trusted Publishing
   works. Never put a key in a workflow file, log, or local config.

With neither configured, the workflow stops before pushing anything.

### The ref model — how the environment gate and the tag interact

GitHub applies an environment's *deployment branches* rule to the **run's own ref**
(`GITHUB_REF` — the "Use workflow from" selection in the Actions UI), not to what the job
checks out. The model is therefore:

- The release is always dispatched **from `main`** — the workflow's first step fails on any
  other ref, so a modified copy of `release.yml` on a side branch can never reach the
  credential.
- The `nuget-release` environment's deployment rule allows **only the `main` branch**.
- **What gets published** is selected by the `tag` input: the workflow checks out exactly
  that tag, verifies it matches `VersionPrefix`, and verifies the tag points at a commit in
  main's history.

**Environment setup (repository settings → Environments):**

| Setting | Value |
|---|---|
| Name | `nuget-release` |
| Deployment branches | Selected branches → `main` only |
| Required reviewers | add yourself (or a second maintainer) |
| Prevent self-review | **off** while there is a sole maintainer — otherwise nobody can approve |

## Package ownership

Packages are published under the nuget.org account that owns the credential above (the
repository owner's account). After the first publication: enable the *"Require package ID
prefix reservation"* consideration by applying to reserve the `ResilientWorkerKit.*` prefix —
optional, but it blocks name-squatting of future subpackages (`.DynamicScheduling.*`). Add a
second owner (or an organization) on nuget.org if the bus factor ever needs to be more
than one.

## Pre-tag checklist (all of these before creating a tag)

- [ ] `main` CI green on the exact commit to be tagged — both OSes, both TFMs, SQL Server leg
      zero skips.
- [ ] `CHANGELOG.md`: the version's section is complete, dated, and the link map updated
      (no "Unreleased" left for the version being tagged).
- [ ] `Directory.Build.props` `VersionPrefix` equals the version being tagged.
- [ ] Migration notes verified against the actual schema diff
      ([persistence.md](persistence.md) — for 2.0.0: lease columns + unique-index dedup).
- [ ] `dotnet list package --vulnerable --include-transitive` clean.
- [ ] Local `dotnet pack` (CI=true) succeeds with package validation; each `.nupkg` contains
      `README.md` and `icon.png`; `sourcelink test` passes on the `.snupkg` PDBs.
- [ ] Clean-consumer smoke test against the local `artifacts/` feed: restore, build, run, on
      net10.0 and net8.0.
- [ ] README install instructions match reality (see "flip the README" below).

## Publish (stage 2B, only after the owner's explicit approval)

1. Tag the approved release commit **on main**: `git tag v2.0.0 <sha> && git push origin v2.0.0`.
2. GitHub → Actions → **Release** → *Run workflow* → **Use workflow from: `main`** → input
   tag `v2.0.0`. The workflow then checks out that exact tag, verifies it against
   `VersionPrefix` and verifies the tag lies on main. Approve the `nuget-release`
   environment gate when prompted.
3. Watch the run: tests → pack → push (with `--skip-duplicate`; symbol packages are pushed
   automatically alongside).
4. Verify on nuget.org, per package: version listed, README rendered, icon shown, the
   dependency groups show framework-matched versions per TFM, and the symbol server accepts
   the snupkg (package page → "Symbols" indicator, or step into the source from a consumer
   with SourceLink enabled).
5. Post-publish consumer test: in a clean project with **only** nuget.org as a source,
   `dotnet add package ResilientWorkerKit --version 2.0.0`, build, run the smoke job.
6. Flip the README: replace the "Not on NuGet yet" note with the install commands, and update
   [roadmap.md](roadmap.md)'s publication section. Create the GitHub release for the tag with
   the CHANGELOG section as its notes and the packed artifacts attached.

## Rollback / unlist plan

NuGet is append-only by design: a published version can be **unlisted** (hidden from search
and from "latest", but still restorable by anyone pinning it) and can only be hard-deleted by
nuget.org support in narrow cases. Plan accordingly:

- **Bad package discovered after publish** (broken assembly, wrong content, defective
  behaviour): fix forward. Publish a patched version (e.g. 2.0.1) and **unlist** the bad one:
  nuget.org → package → *Listing* → uncheck "List in search results", per package, per
  version. Note it in the CHANGELOG ("2.0.0 unlisted: reason"), the way 1.1.0's known defect
  is on record.
- **Never** re-push different bits under the same version — nuget.org forbids it, and
  `--skip-duplicate` in the workflow means an accidental re-run of a published tag is a
  no-op, not an overwrite.
- **Secret suspected leaked:** revoke the API key on nuget.org immediately (Trusted
  Publishing has nothing long-lived to leak); rotate; audit the package's recent versions.
- **Partial publish** (some of the five packages pushed, then a failure): re-run the same
  workflow on the same tag — `--skip-duplicate` makes it idempotent; already-pushed packages
  are skipped, missing ones are pushed.
- The five packages version together: a fix to any of them bumps and republishes **all
  five**, keeping the same-version dependency graph intact.

## What this stage explicitly did not do

No tag exists for 2.0.0, no GitHub release was created, nothing was pushed to NuGet, and no
package ID was reserved. Those are stage 2B actions, gated on the owner's
`NUGET YAYININI ONAYLIYORUM`.
