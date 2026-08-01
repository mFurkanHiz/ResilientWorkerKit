# Test Plan

Two test projects:

- **ResilientWorkerKit.UnitTests** — fast, deterministic, no real time waits. Schedule
  calculations are tested as pure functions; engine flows use `FakeTimeProvider`; stores use the
  in-memory implementations.
- **ResilientWorkerKit.IntegrationTests** — a real .NET Generic Host, real DI scopes, EF Core on
  SQLite (temp files for restart scenarios), an in-process fake HTTP server, multiple real
  registered jobs, and host stop/start cycles.

## Unit test areas

### Execution & failure isolation
- Failing job does not stop the host / the scheduler loop / other jobs
- Success ⇒ `Completed`; permanent error ⇒ `Failed` (no retry); cancellation ⇒ `Cancelled`;
  total timeout ⇒ `TimedOut`; duration recorded
- `OperationCanceledException` on shutdown logged as Information, not Error

### Retry
- Transient retried, permanent not; backoff sequence exponential; jitter within bounds
- Retry count honored; `ExecutionId` stable across attempts; `AttemptNumber` increments
- `Retry-After` hint respected; retries exhausted ⇒ `Failed` + optional dead letter
- Job runs again at next occurrence after a failed execution

### Checkpoints
- Get/save/clear round-trip (typed, custom JSON state)
- Failed batch does not advance checkpoint; corrupted checkpoint ⇒ safe `Misconfigured` error
- Continuation token persisted and restored

### Idempotency
- Same key not processed twice; concurrent `TryAcquire` — exactly one winner
- Completed record not reprocessed; failed record re-acquirable per policy; expired record reusable

### Overlap & locking
- `SkipNewExecution` skips while running; `QueueSingleExecution` queues at most one
- Different jobs run in parallel; lock released after exceptions

### Schedules (pure functions; DST/leap-year matrices)
- Interval, fixed-delay (difference documented & tested), cron, daily, weekly, one-time,
  run-on-startup
- Monthly: day 5 fires once; day 31 + `SkipMonth` skips February; day 31 +
  `RunOnLastAvailableDay` runs Feb 28/29; leap years; last-day-of-month across month lengths;
  identity `job:yyyy-MM` completed ⇒ not re-run after restart; next month runs again
- Time zones: local→UTC conversion, DST spring-forward (invalid time) and fall-back (ambiguous
  time) policies; no double-fire
- Misfire: Skip / RunImmediatelyOnce (only once, restart-safe) / RunIfWithinTolerance (inside vs
  outside tolerance) / RescheduleFromNow

### HTTP
- Typed client wiring; 5xx retried, 400 permanent, 429 + `Retry-After` honored (classifier level)
- Timeout and cancellation propagation
- Correlation-ID and Idempotency-Key headers added; Authorization never logged; masking works
- Pagination helpers return correct continuation tokens

### Health
- Success ⇒ Healthy; consecutive failures ⇒ Degraded; sustained failure ⇒ Unhealthy
- Never-run job is not Unhealthy; running state and next-occurrence reported; stuck detection

### Graceful shutdown
- No new executions after stop; running job receives token; grace period honored
- Checkpoint not advanced by an interrupted batch; interrupted execution recorded
  `Cancelled`/left for `Abandoned` recovery; lock released

## Integration test scenarios

1. **End-to-end resume (the flagship scenario)**
   1. Host starts (SQLite file DB), `ReservationSyncJob` processes page 1, checkpoint saved
   2. Page 2 fails repeatedly, retries exhaust ⇒ execution `Failed`
   3. Host keeps running; `NotificationDispatchJob` continues and succeeds
   4. Host stops gracefully; host restarts on the same database
   5. Sync job resumes from page 2 (not page 1); previously processed reservation produces no
      second side effect (idempotency); execution completes
2. **Restart recovery** — `Running` record left behind ⇒ marked `Abandoned` on next start
3. **Monthly identity across restarts** — completed monthly occurrence not re-created; missed
   occurrence with `RunImmediatelyOnce` created exactly once across two restarts
4. **Failure isolation under the real host** — one job throwing every time; host + second job
   unaffected over multiple occurrences
5. **EF Core stores on SQLite** — schema creation, unique-index idempotency race (two concurrent
   acquires), execution history persistence, checkpoint round-trip
6. **Fake HTTP server** — scripted 500,500,200 sequence retried by the resilience pipeline;
   429 with `Retry-After`; permanent 400 not retried; masked logging asserted

## Conventions

- No `Thread.Sleep`; real-time waits bounded to short polling with timeouts only in
  integration tests
- `FakeTimeProvider` for every schedule/backoff computation
- Coverage is collected in CI (coverlet) and reported honestly — no inflated numbers
