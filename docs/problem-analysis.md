# Problem Analysis

## Methodology

Before any code was written for ResilientWorkerKit, a **read-only, clean-room analysis** of
several existing private .NET worker-service codebases (a fleet of ~15 near-identical
`BackgroundService` executables plus their shared HTTP/data-access plumbing) was performed.

Rules of the analysis:

- No file in the analyzed repositories was modified, and no code was copied.
- Findings were recorded only as **generalized patterns** — no company, product, domain or
  business-rule specifics appear in this document or anywhere in this repository.
- The goal was to answer one question: *which reliability problems do teams re-create every time
  they hand-roll background workers, and which of them can a small library eliminate by
  construction?*

The sample domain used everywhere in this repository (reservation reconciliation) is entirely
fictional and was designed after this analysis, not taken from it.

## Recurring worker use-cases observed

| Use-case | Description |
|---|---|
| Pending-work polling & forwarding | Poll an internal API/DB for records in a pending state, push each to an external partner API, write status back |
| Reference-data synchronization | Periodically fetch a full lookup/master dataset and replace or merge it into a local table |
| Status reconciliation of in-flight items | Poll a third-party API for the status of previously submitted items and reflect transitions locally |
| Financial reconciliation & settlement | Verify pending transactions against an external provider; record settled results; issue irreversible operations |
| Notification dispatch | Drain a queue of outbound email/SMS/push messages through third-party gateways |
| Remote state-transition control | Evaluate records against business rules and call remote complete/cancel/expire endpoints |
| Update-by-replace against limited APIs | Emulate updates by deleting and re-creating remote records |
| Scheduled daily / time-of-day jobs | "Run at 02:00 every day" approximated with polling loops and in-memory next-run fields |
| Data retention & expiry housekeeping | Delete rows older than a retention window; expire stale pending records |
| Bulk import of large external datasets | Fetch an entire remote dataset unpaged, buffer in memory, wholesale-replace a staging table |
| Telemetry / snapshot ingestion | High-frequency polling writing one snapshot row per entity per cycle |
| Per-cycle authentication | Acquire a fresh bearer token at the top of every cycle (or per item) |
| Audit-trail recording | Write an integration-outcome row for every processed item |
| Stuck-item alerting | Detect items stuck in a non-terminal state and notify operators inline |
| Worker fleets on hand-rolled plumbing | Many sibling executables copy-pasting the same loop/delay/error-handling scaffolding |

These use-cases directly motivated the feature set: polling schedules, calendar schedules with
time zones, checkpoints/cursors, idempotency keys, retry with classification, dead-lettering,
health checks and HTTP integration helpers.

## Problem taxonomy

Frequency = how many of the 8 independent analysis groups reported the pattern.

### Failure isolation

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| One catch-all around the whole iteration destroys per-step/per-item isolation | 7/8 | high | A single poison item aborts the whole batch; whole cycles silently abandoned |
| Unhandled exceptions escape `ExecuteAsync` and stop the host | 5/8 | high | One transient blip becomes a full outage until manual restart |
| Fire-and-forget async timer callbacks (`async void` semantics) | 2/8 | high | Exceptions crash the process with no useful log |
| Lower layers swallow exceptions and return normal results | 5/8 | high | "Mark as processed" runs after failed work → silent data loss, false completion |
| Unvalidated external payloads consumed without guards | 7/8 | high | Empty/malformed responses throw far from the cause; outages misdiagnosed as data bugs |
| Silent fallbacks/sentinels drive wrong irreversible actions | 3/8 | high | Wrong state transitions indistinguishable from correct processing |

### Retry

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| No retry, backoff, or circuit breaker anywhere; transient == permanent | 8/8 | high | Transient blips lose whole cycles; outages hammered at full rate |
| Implicit infinite retry of poison items, no attempt cap, no dead-letter | 7/8 | high | Poisoned records generate unbounded retry traffic forever |
| Hand-rolled unbounded hot retry loops (no delay, no cancellation) | 2/8 | high | Tight-loop hammering of third-party services; unresponsive shutdown |
| Failure paths advance state so failed work is never re-driven | 2/8 | medium | Failed runs/items permanently lost with no record |

### Scheduling

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| Fixed post-work delay ⇒ unbounded schedule drift; no fixed-rate option | 8/8 | medium | Cadence silently degrades; narrow windows missed entirely |
| Early-exit paths (`continue`) skip the bottom-of-loop delay ⇒ 100% CPU busy loops | 3/8 | high | Thousands of auth requests per second against remote endpoints |
| Wall-clock / hardcoded-UTC-offset / DST-unsafe time handling | 8/8 | high | DST transitions skip or double-fire jobs; retention math breaks |
| Daily scheduling approximated with polls + clock-window gates | 4/8 | high | Double-runs in one window, hours-late fires, silently skipped days across restarts |
| No jitter or startup stagger across co-deployed workers | 6/8 | medium | Synchronized load spikes after every redeploy |
| Shared mutable next-run state raced by concurrent timers, no overlap guard | 2/8 | high | Non-deterministic starvation and double-runs |
| Interval constants with misleading unit names | 4/8 | low | Tuning by name changes the schedule by 1000× |

### Checkpoint / resume

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| No durable run state, checkpoint, cursor or run history at all | 8/8 | high | Interrupted batches restart blind; "did last night's run complete?" unanswerable |
| Destructive full-table replace without transaction or payload validation | 5/8 | high | A transient empty response wipes the destination table |
| Failure paths corrupt the only persisted progress signal | 1/8 | high | Failed items permanently mislabeled as processed |

### Idempotency

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| Multi-step side effects non-atomic; status flipped only after external write | 7/8 | high | Re-fetch on next cycle re-executes side effects → duplicate bookings/ledger entries |
| No idempotency keys / dedup tokens on outbound side-effecting calls | 6/8 | high | Every retry creates genuine duplicates downstream |
| No lock, lease or single-instance guarantee | 8/8 | high | Two instances double-execute every side effect |
| Update emulated as delete-then-recreate with no compensation | 2/8 | high | Failure between the calls permanently destroys remote records |
| Side effects re-executed unconditionally on every poll | 3/8 | high | Notification storms; balances repeatedly adjusted |
| Work selection by time window instead of processed-state predicate | 1/8 | high | Completed items reprocessed; stragglers abandoned at month end |
| Read-then-act duplicate checks with race windows | 2/8 | medium | Overlapping runs both observe "not present" and both push |

### HTTP integration

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| HTTP client instantiated per call, never disposed; DI factories registered but unused | 8/8 | high | Socket/port exhaustion, stale DNS, intermittent failures under load |
| No timeout on any outbound HTTP/DB call | 8/8 | high | One hung endpoint stalls the loop forever; worker looks alive |
| Response status never checked before deserialization | 8/8 | high | Non-2xx treated as success; outages surface as null-reference exceptions |
| Success inferred from reason phrases / body flags / non-null responses | 4/8 | high | Success misclassified as failure (feeding retry loops) and vice versa |
| Tokens re-fetched every cycle/item; no cache, expiry tracking, refresh-on-401 | 8/8 | medium | Identity-endpoint hammering; "Bearer null" headers; mid-batch expiry failures |
| Blocking sync I/O behind async signatures | 4/8 | medium | Thread-pool starvation; failures unobservable |
| Undisposed responses; process-global TLS state mutated per call | 3/8 | medium | Handle leaks; global security state ripples |
| Unbounded, unpaged fetches with no size caps | 3/8 | medium | Memory/duration/blast-radius scale with upstream growth |

### Logging & observability

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| Failures logged at Information with the exception object discarded | 7/8 | high | Days of failures invisible to severity-based alerting; undiagnosable |
| Exception message used as the structured-logging template | 6/8 | high | Logging itself throws on brace characters; aggregation destroyed |
| Console writes bypass the logging pipeline in shared layers | 8/8 | high | Under a service host all diagnostics silently discarded |
| Interpolated strings destroy structured logging | 7/8 | medium | Logs unfilterable and unindexable |
| No metrics, health signal, heartbeat, correlation IDs or run summaries | 8/8 | high | Healthy-idle vs stuck vs silently-failing indistinguishable |
| Business logic gated behind a log-level check | 2/8 | high | Raising the log level silently turns the worker into a no-op |
| Inline alerting with no dedup; misleading log statements | 3/8 | medium | Duplicate alert spam; wrong operator beliefs |

### Lifecycle

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| Cancellation token never propagated into real I/O | 8/8 | high | Shutdown lands between side effect and status flip — the exact duplicate-producing window |
| No graceful shutdown: no drain, no grace budget | 8/8 | high | Every deploy force-kills mid-item; partial writes with no record |
| Shutdown cancellation conflated with failure | 5/8 | medium | Clean stops logged as application errors |
| Hard process exit called inside the loop | 2/8 | high | Service silently becomes a one-shot with a success exit code |
| `ExecuteAsync` returns immediately, orphaning undisposed timers | 2/8 | high | Callbacks fire during/after host teardown |
| Real work in constructors; no startup readiness validation | 4/8 | medium | Misconfiguration surfaces as an opaque construction crash |

### Configuration

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| Environment selection via compile-time constants | 8/8 | high | Promotion requires a rebuild; prod pointed at test backends by a mis-edit |
| Settings files excluded from build output (dead configuration) | 6/8 | high | Editing settings has no effect; hidden constants govern production |
| Config system bypassed by hand-parsing JSON from disk | 7/8 | medium | Overlays/secrets never apply; empty-string fallbacks embedded into partner calls |
| Dependencies `new`-ed directly; no options binding or validation | 8/8 | medium | Untestable; no central resilience; opaque startup crashes |
| Operational parameters as undocumented magic numbers | 8/8 | medium | Nothing tunable without a redeploy |
| Environment switching by comment toggling; dev config → prod DB | 5/8 | high | Local runs mutate production data |
| Test-tuned values and dead code active in production | 4/8 | medium | Test timing and half-disabled safety checks ship live |

### Security

| Problem | Freq | Severity | Consequence |
|---|---|---|---|
| Plaintext credentials committed per environment (incl. commented alternates) | 8/8 | high | Repo read access yields credentials for every environment |
| Secrets as compile-time constants recoverable from binaries | 8/8 | high | Rotation requires redeploy; secrets exposed to source/binary access |
| Credentials & personal data written to console/log output | 7/8 | high | Tokens and identity data land in log archives |
| SQL built by string concatenation of untrusted remote data | 4/8 | high | Injection fed directly by a third-party system |
| DB transport encryption explicitly disabled | 6/8 | high | Credentials and rows cross the network in the clear |
| Credentials in URL query strings | 4/8 | high | Secrets recorded by proxies and access logs |
| Injection-prone hand-built XML/SOAP envelopes | 2/8 | medium | Markup content breaks or injects into partner requests |

## What a reliability library must therefore provide

- **Scheduling** — own the loop entirely: interval *and* fixed-delay semantics, cron and
  calendar schedules bound to named time zones with correct DST handling, run-on-startup,
  misfire detection with explicit catch-up policies, and overlap policies. The inter-run wait
  must be structurally impossible to bypass.
- **Failure isolation** — own the invocation boundary so no job exception can reach the host or a
  timer thread; explicit typed outcomes instead of swallowed voids; per-execution scopes.
- **Retry** — declarative policies with backoff + jitter and hard attempt caps; failure
  classification (transient/permanent/poison); durable attempt tracking; dead-letter quarantine.
- **Checkpoint/resume** — a durable run-state store out of the box: execution history,
  last-success markers, typed checkpoints/cursors so interrupted batches resume, not restart.
- **Idempotency** — persisted idempotency keys acquired *before* side effects; atomic acquire
  semantics backed by unique constraints; documented limits (no distributed transactions).
- **HTTP integration** — factory-managed pooled clients, mandatory timeouts, cancellation
  propagation, status validation before deserialization, token caching with refresh, paging
  helpers, `Retry-After` awareness.
- **Observability** — correct-by-construction structured logging (constant templates, exceptions
  as objects, severity discipline), correlation scopes, metrics, per-job health with stuck
  detection. Observability configuration must never alter business behavior.
- **Lifecycle** — cooperative cancellation threaded through everything, drain semantics with a
  grace budget, cancellation as a distinct clean outcome, startup validation that fails fast.
- **Configuration** — strongly-typed, validated, fail-fast options; no magic literals required.
- **Security** — no secrets in source; redaction by default in every log the library emits;
  header-based auth helpers; documented rules for what may enter checkpoints/idempotency
  keys/dead letters.

Every feature in ResilientWorkerKit traces back to at least one row in the tables above.
