# Decision log

Architectural decisions, the alternatives that were considered, why they were rejected, and
what would have to change for a decision to be revisited. Newest first within each stage.
Status values: **proposed** (analysis written, not yet final), **accepted** (binding),
**superseded** (replaced by a later entry).

---

## D-007 — Core version for the next release: 2.0.0

**Status:** accepted — the D-001 implementation is complete; the lease contract replaced
`IPendingOccurrenceStore`'s claim-as-delete shape, which is a breaking public API change.

With D-001 choosing a breaking reshape of `IPendingOccurrenceStore`, SemVer requires a major
version. 1.1.2 (patch) cannot hold an API/schema change; 1.2.0 (minor) would require carrying
a compatibility path whose entire purpose is preserving a data-loss window, which D-001
rejects on the merits, not to dodge a number. The stage 0 estimate of 1.2.0 is therefore
revised: **2.0.0**.

A major bump one day after 1.0.0 is unusual, and the changelog says why honestly: the audit
found a durability hole in a v1.1 contract before any NuGet publication; the contract was
fixed properly, and the version says so. v1.1.0 and v1.1.1 remain in history untouched.

**Revisit if:** D-001 is revised to a non-breaking option before release.

---

## D-006 — SQL Server compatibility: prove it in CI or stop claiming it

**Status:** accepted (stage 1).

**Decision:** add env-var-gated SQL Server integration tests (`RWK_SQLSERVER_CONNECTION`);
the CI Linux leg provides a SQL Server service container and runs them on every push. Where no
server is available (local machines without Docker), the tests skip with an explicit reason.
Documentation states the claim exactly as it is backed: SQLite verified everywhere, SQL Server
verified in CI.

**Alternatives.** *Soften the claim instead* — acceptable fallback per the stage plan, but the
EF Core model was designed for SQL Server (length limits, UTC `DateTime` columns), so testing
it is cheap and strictly better. *Testcontainers* — pulls a heavier dependency into the test
project and still needs Docker; a CI service container plus a plain connection string does the
same with less machinery.

**Revisit if:** the service container makes CI unacceptably slow or flaky; then the fallback is
softening the claim, never leaving an untested claim in place.

---

## D-005 — Repeating: lazy arithmetic schedule instead of a count cap

**Status:** accepted (stage 1).

**Problem.** `Repeating(startAt, every, count)` eagerly materializes `count` instants into the
`ExplicitTimesSchedule` array. `count = int.MaxValue` is an OOM at configuration time;
`every * i` can throw `OverflowException`; `startAt + …` can throw
`ArgumentOutOfRangeException` past `DateTimeOffset.MaxValue` — raw exceptions, not
`JobConfigurationException`.

**Options considered.**

| Option | Verdict |
|---|---|
| Fixed safe maximum (e.g. 10 000) | Rejected: any constant is arbitrary; 10 000 × 16 bytes is harmless, 10 000 000 is not, and the number encodes no principle. |
| Configurable limit | Rejected: pushes an arbitrary decision onto the user and adds an option nobody can reason about. |
| Keep eager array, wrap overflow | Rejected: fixes the exceptions but keeps O(count) memory for what is a closed-form arithmetic progression. |
| **Lazy finite schedule** | **Chosen.** A `RepeatingSchedule` computes occurrence *i* as `startAt + i × every` on demand: O(1) memory, no cap needed at all, misfire scanning stays bounded by the engine's existing catch-up limit. |

**Validation that remains (all `JobConfigurationException`):** `every ≥ 1 second` (sub-second
gaps would collide at the second-precision occurrence identity — see D-004), `count ≥ 1`, and
`startAt + (count−1) × every` must stay inside the `DateTimeOffset` range (computed with
overflow checks at configuration time).

**Compatibility.** `Repeating` no longer routes through `ExplicitTimesSchedule`; the identity
format (`at:<utc-second>`) is unchanged, so occurrences completed under 1.1.x keep their
identity under the new schedule. Behavioural change (formerly accepted inputs now failing
fast) is listed in the CHANGELOG.

**Revisit if:** a real use case needs non-arithmetic finite sequences at scale; that is
Dynamic Scheduling's planner, not Core's builder sugar.

---

## D-004 — Explicit times: fail fast on duplicates and second-precision collisions

**Status:** accepted (stage 1).

**Problem.** Two distinct cases, both previously silent:
(a) the exact same timestamp passed twice — collapsed by `Distinct()`;
(b) distinct timestamps within the same UTC second — identity is `at:yyyy-MM-ddTHH:mm:ssZ`
(second precision), so the second occurrence is silently skipped as a duplicate after the
first completes. Silent collapse and silent skip both violate "planned actions must happen or
fail loudly".

**Decision.** `ExplicitTimesSchedule` throws `JobConfigurationException` for both cases, with
messages that name the offending instants and distinguish the two cases. The identity format
is **not** changed: widening precision would change the identity of every explicit-time
occurrence, and an occurrence completed before the upgrade would no longer match its recorded
identity — the engine would run it again. A completed sale-opening running twice because of a
library upgrade is precisely the class of bug this project exists to prevent.

**Alternatives.** *Silently collapse (status quo)* — rejected, hides a configuration error.
*Sequence-suffixed identities for same-second instants* — rejected: changes identity shape,
and "run the same job three times in one second, distinctly" is not a scheduling need Core
serves; Dynamic Scheduling models intentional same-instant occurrences with its own
occurrence keys.

**Revisit if:** the identity format ever changes for an unrelated, unavoidable reason; the two
validations can then be reconsidered together with a documented migration.

---

## D-003 — Deterministic pending-occurrence ids

**Status:** accepted (stage 1).

Pending rows previously used random GUID ids, so nothing prevented the same logical follow-up
being queued twice (for example by crash-recovery re-planning after a re-run, or by two hosts
planning the same follow-up). **Decision:** uniqueness of the logical occurrence is enforced
by the database with a **unique index on (`JobId`, `IdentityToken`)**; the row id stays an
opaque GUID. `AddAsync` treats a uniqueness violation as "already queued" and reports it as
`false` rather than throwing — the database is the cross-process arbiter of "this follow-up
exists".

**Rejected alternatives.** *An `ExistsForOriginAsync` store query before every add* — racy
(check-then-act across processes), one extra round trip, more contract surface. *Making the
row id itself the logical identity* (the first draft of this entry) — rejected by the
pre-implementation review: `{jobId}:{identity}` can reach 500 characters, past SQL Server's
900-byte clustered-key limit, whereas the same pair as a *nonclustered* unique index fits the
1700-byte limit at the declared column sizes; and an opaque id keeps its meaning when future
sources define identities of their own.

**Migration note (binding for the persistence docs):** 1.1.x could legitimately produce
duplicate (`JobId`, `IdentityToken`) pairs — its `ReturnToQueueAsync` re-added a claimed row
under a fresh GUID. Creating the unique index on an existing table must therefore be preceded
by a documented deduplication step (keep the earliest `CreatedAtUtc` row per pair, delete the
rest).

**Revisit if:** a source other than follow-up retries needs multiple rows for the same logical
occurrence; that source would then define its own id scheme, which the contract permits.

---

## D-002 — Crash during execution: lease re-delivery, plus opt-in chain continuation

**Status:** accepted (stage 1). This is blocking problem **B** from the stage plan (problem
**A**, the claim-window loss, is D-001).

**The two crash cases and what happens after this stage:**

1. **Crash while a *pending-sourced* execution runs** (a follow-up, or later any queued
   occurrence): the row is still leased when the process dies. The lease expires, the row
   becomes acquirable again, and the occurrence **re-runs at the same follow-up ordinal**.
   The completed-identity check still prevents re-running anything that recorded completion.
   This is at-least-once *by contract*: a durably planned action re-delivers until a terminal
   outcome is durably recorded. It requires no policy, because the row's existence is the
   user's explicit request that this action eventually happen.
2. **Crash while a *schedule-sourced* execution runs** (the origin run): the execution record
   is marked `Abandoned` at startup, and — as in 1.0 — the occurrence is **not** re-executed
   (any-record misfire suppression). Without further action a `RetryLater` chain that never
   got to plan its first follow-up ends silently. **Decision:** a new opt-in policy,
   `FollowUpRetryOptions.ContinueAfterAbandoned` (default **false**). When enabled, per-job
   recovery scans recent `Abandoned` executions, derives the origin and follow-up ordinal from
   the recorded `ScheduledExecutionId` (`…+followup-N`, origin = ordinal 0), and queues the
   next ordinal within `MaxAttempts` via the deterministic id from D-003 (so re-scanning or a
   second host cannot double-queue).

**Why the default is off.** An `Abandoned` record means the process died mid-execution — the
external call may have succeeded with its response unobserved. Automatically re-executing
every abandoned run would convert every crash into a potential duplicate side effect for jobs
that never asked for durability. The asymmetry with case 1 is deliberate: queuing durable work
(`RetryLater`, planned occurrences) *is* the opt-in for at-least-once re-delivery; a plain
scheduled run has made no such promise. Documentation states plainly that both
`ContinueAfterAbandoned` and lease re-delivery presuppose idempotent job bodies, and points at
the existing checkpoint/idempotency primitives as the mechanism.

**Rejected alternatives.** *Auto-retry every `Abandoned` execution* — explicitly ruled out
(duplicate side effects for non-opted-in jobs). *Persist "chain intent" in a separate table* —
more schema for what the execution record already encodes in its identity. *Do nothing but
document* — leaves blocking problem B open.

**Amendments from the pre-implementation adversarial review:**

- **Completion is gated on the follow-up-planning outcome.** The review's one blocking
  finding: planning a follow-up can fail to write durably (a transient store outage, logged
  and swallowed), after which an unconditional `CompleteAsync` deletes the ordinal-N row and
  the chain is lost with no recovery path. Planning therefore reports
  `Planned / NotNeeded / WriteFailed`, and the engine completes the row only for the first
  two. On `WriteFailed` the lease is left to lapse (or released), so the occurrence
  re-delivers, re-runs at the same ordinal, and re-plans — the unique index (D-003) makes the
  re-plan idempotent. A duplicate run of ordinal N is the accepted at-least-once corner; a
  lost chain is not.
- **A `Cancelled` outcome never completes the row.** Cancellation (graceful shutdown, and
  cancellation generally) is a non-terminal outcome *for a planned action*: the work did not
  happen. The lease is released so the occurrence re-delivers — on the next start, or on
  another host. Deleting the row on shutdown would turn every redeploy into a lost planned
  action, which is the exact defect class this stage exists to remove.
- **The recovery scan classifies records by `TriggerType`, not by parsing identity tokens.**
  A custom `IJobSchedule` may legitimately emit identity tokens containing `+followup-`;
  trigger type is engine-assigned and unforgeable. Ordinal-0 candidates are records whose
  trigger is anything but `follow-up`, with status `Abandoned` or `Failed` (`Failed` covers
  the narrow crash window between the terminal record and the follow-up's durable write).
  `Cancelled` ordinal-0 records are deliberately *not* candidates: cancellation is an
  explicit, recorded decision in this library's semantics, and the schedule-occurrence path
  applies any-record misfire suppression to it; changing that is out of scope here and would
  need its own decision.
- **No staleness cutoff on chain continuation, deliberately.** A planned action's contract is
  "late is better than never" — the same reasoning as `RunImmediatelyOnce` being the default
  misfire policy for planned schedules. The scan is bounded by the same recent-200 window the
  anchor recovery uses; both boundaries are documented rather than silent.
- **Clock discipline is documented, not hand-waved:** stores never read their own clock
  (`nowUtc` is a parameter); heartbeat renewal runs at lease-duration ∕ 3 from inside the
  loop that owns the run; cross-host clock skew must stay under that same third for the
  renewal guarantee to hold, which is stated in the persistence documentation. A renewal
  failure (lease lost to another owner) is logged and the run is *not* killed — killing it
  cannot undo side effects already performed, and the duplicate-execution corner is the
  documented at-least-once trade.

**Amendments from the post-implementation adversarial review** (four lenses over the final
diff, findings independently verified before acceptance):

- **Origin plan-write failures are retried in-process.** The outcome-gated completion above
  protects runs that hold a durable row; an *origin* run has no row, so a transient `AddAsync`
  failure used to lose the chain while the process kept running — reachable without any crash.
  The unwritten follow-up is now stashed in the loop and re-attempted with a 5-second backoff
  until the write lands (idempotent via D-003's unique index). Process death before the write
  still loses the chain unless `ContinueAfterAbandoned` is on; that residual boundary is
  documented rather than silent.
- **Every pending-store failure path backs off; every recovery path forces its own wake.** A
  throwing acquire (or stale-row cleanup) now defers pending work by 5 seconds instead of
  spinning compute–fire–throw at full speed; a throwing queue read schedules a forced re-read
  instead of masquerading as "queue empty" and sleeping forever. The decline cooldown became
  the general pending-backoff.
- **Lease expiry is inclusive.** `GetNextAsync` surfaces a leased row *at* its expiry, so the
  acquire predicate treats a lease expiring at `now` as expired (`<=`, both stores) — an
  exclusive boundary left a woken scheduler spinning one instant short of acquirability,
  observable under a fake clock advanced exactly to the expiry.
- **The schedule anchor never moves backwards** (a pre-existing 1.x defect the review caught:
  a queued-overlap refire rewrote the anchor to its older occurrence time, re-deriving
  already-skipped occurrences as misfires — double execution under `RunImmediatelyOnce`).

**Revisit if:** execution records ever stop encoding the ordinal in `ScheduledExecutionId`;
the ordinal would then need its own column (additive schema change).

---

## D-001 — Pending-occurrence store: breaking lease contract (option A)

**Status:** accepted (stage 1). This is blocking problem **A**: after `TryClaimAsync`
(a committed `DELETE`) and before the runner writes the execution record, a crash loses the
planned occurrence permanently — no row, no record, no dead letter.

**Requirement for any fix:** a claim must be revocable until an execution outcome exists
durably. A delete is not revocable; therefore the claim must become a *lease* — a state
transition the store can expire — and completion (the delete) moves to after the terminal
execution record.

### Options

**A — reshape `IPendingOccurrenceStore` in place (breaking).**
`TryClaimAsync` is replaced by lease operations; `GetNextAsync` gains visibility semantics
(pending, or lease expired). One contract, one engine path, compile-time breakage for any
custom store — which is the *desired* failure mode, because a custom store that still deletes
on claim silently lacks the guarantee the engine now assumes.
Crash-safety: full. API complexity: lowest (one contract). Consumer impact: custom stores
must be rewritten, loudly, at compile time; the two shipped stores are rewritten here.
Migration: additive columns + documented SQL. Old-store safety: n/a — the old shape ceases to
exist. Test cost: lowest (one path). SemVer: **major**.

**B — new `IPendingOccurrenceLeaseStore`, obsolete the old interface.**
The engine must handle both registrations, so the delete-claim path — the data-loss path —
survives as supported behaviour behind an `[Obsolete]` warning. Two contracts, two engine
paths, double the tests, and a user who ignores the warning keeps the crash window while
believing the library durable. SemVer: minor. Rejected: preserves the defect as a feature.

**C — new lease contract as the engine's contract, compatibility adapter wraps old stores.**
Single engine path (good), but the adapter cannot manufacture durability the old contract
lacks: wrapping delete-claim either deletes (window intact) or holds the row without marking
it (double-run across processes). The adapter would exist solely to make an unsafe store look
supported. SemVer: minor. Rejected: compatibility theatre.

**D — default interface methods / capability interfaces on the existing contract.**
A DIM cannot implement lease semantics — it has no storage access, so any default body must
emulate leases via delete-claim, which is option C's flaw hidden *inside* the contract.
Capability probing (`is ILeaseCapable`) is option B with runtime dispatch instead of types.
Rejected.

### Decision

**Option A.** The owner's instruction explicitly permits a major version when the cleanest
design is breaking, and forbids carrying two parallel store abstractions merely to avoid one.
There are no NuGet consumers yet (nothing has been published), so the real-world cost of the
break is as low as it will ever be, and every alternative preserves an unsafe path.

### Contract shape (implemented in stage 1)

- `AddAsync` — unchanged semantics, plus: a unique-index conflict on
  (`JobId`, `IdentityToken`) means "already queued" (D-003) and is reported as
  <code>false</code>, not thrown.
- `GetNextAsync` — the occurrence with the earliest *effective* time: an unleased or
  expired-lease occurrence is effective at its due time; an occurrence under an unexpired
  lease is surfaced no earlier than its lease expiry, with its lease fields populated.
  Returning leased rows is load-bearing — it is what lets a scheduler sleep *until* another
  owner's lease expires instead of polling, and what makes expired-lease recovery
  visibility-based instead of a background sweeper. (The original text of this entry said
  "acquirable only"; that wording was found self-contradictory by the pre-implementation
  adversarial review — a schedule-less job would sleep forever with no wake at expiry — and
  was corrected before any code was written.)
- `TryAcquireLeaseAsync(id, owner, duration)` → lease token or null; atomic single-winner via
  conditional update (`WHERE` unleased-or-expired), the database picks the winner.
- `TryRenewLeaseAsync(id, token, duration)` — heartbeat; the engine renews while the execution
  runs, so a live run's lease never expires under it (renewal cadence: duration ∕ 3).
- `CompleteAsync(id, token)` — the delete, valid only for the token holder, called after the
  terminal execution record and follow-up planning.
- `ReleaseAsync(id, token)` — immediate return to acquirable (used when the runner declines
  because the job lock was unavailable), valid only for the token holder. Replaces 1.1.1's
  delete-then-re-add `ReturnToQueueAsync`, which had its own crash window.
- `CountAsync` — unchanged.

Atomicity mechanism: conditional `ExecuteUpdate`/`ExecuteDelete` with the token (or
expiry predicate) in the `WHERE` clause; affected-rows decides the winner. Portable across
SQLite and SQL Server, no concurrency-token column, no transaction juggling. Expired-lease
recovery is *visibility-based* (the `GetNextAsync`/acquire predicates) rather than a
background sweeper: no extra moving part, works identically cross-process.

**Scope boundary (binding):** this makes the *pending-occurrence capability* multi-instance
safe (single lease owner, expiry recovery, owner-checked complete/release). It does **not**
make the whole library multi-instance safe — job locks, startup recovery, scheduled-occurrence
dedup and wake/polling remain single-instance concerns, stated in
[limitations.md](limitations.md).

**Revisit if:** a store appears whose backend cannot express conditional updates (then the
contract needs a documented transactional fallback), or NuGet-era consumers exist and a future
reshape is contemplated — at that point option A's calculus no longer applies.

---

## Stage 0 decisions inherited from the audit acceptance

- Core is one product line; v1.1.1 is the base (owner decision, 2026-08-02).
- Planned-time features stay in Core (owner decision, 2026-08-02).
- Dynamic Scheduling: separate packages, same repository, dependency direction
  Dynamic → Core only (owner decision, 2026-08-02).
- v1.1.0 is never published to NuGet (owner decision, 2026-08-02).
- Exactly-once is never claimed (owner decision, 2026-08-02).
