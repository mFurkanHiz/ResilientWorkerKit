# ReservationReconciliation.Worker (sample)

A complete, self-contained ResilientWorkerKit host built around an entirely fictional
**reservation reconciliation** domain. It hosts six jobs, persists state in SQLite, and
embeds its own fake reservation API so the failure scenarios are reproducible offline.

```bash
dotnet run --project samples/ReservationReconciliation.Worker
```

- `http://localhost:5210/` — per-job health snapshots and the demo counters
- `http://localhost:5210/health` — the ASP.NET Core health check
- `http://localhost:5210/fake-api/reservations` — the embedded fake API

## The jobs

| Job | Schedule | Purpose |
|---|---|---|
| `reservation-sync` | every 5 minutes + on startup | Pages through the fake API, reconciles reservations, checkpoints per page, guards every item with an idempotency key |
| `notification-dispatch` | 1 minute after each completion + on startup | Posts a notification with an `Idempotency-Key` header; keeps succeeding while the sync job fails |
| `daily-reconciliation` | daily 02:00 Europe/Istanbul | Calendar schedule with a time zone |
| `weekly-cleanup` | Sundays 03:00 Europe/Istanbul | Weekly schedule |
| `monthly-billing` | day 5, 10:30 Europe/Istanbul, `SkipMonth` | One occurrence per month, identity `monthly-billing:2026-08` |
| `end-of-month-settlement` | last day of month, 23:00 Europe/Istanbul | Correct on 28/29/30/31-day months |

## The scripted failures

The embedded API (`Api/FakeReservationApiState.cs`) deliberately misbehaves so every reliability
feature is observable in one run:

| Scripted behavior | What it demonstrates |
|---|---|
| Page 2 returns HTTP 500 on its first two calls, then succeeds | Transient failures retried by the HTTP resilience pipeline |
| Page 3 returns HTTP 429 with `Retry-After: 1` on its first call | Rate-limit handling that honors the server's delay |
| Reservation 104 has `nights = -1` | A permanently invalid item is dead-lettered and the batch continues instead of being poisoned |
| Reservation 101 appears again on page 3 with the same version | Idempotency suppresses the second side effect |
| The checkpoint is saved only after a page completes | Resume from the exact page after a crash or restart |

## Watching resume and idempotency work

Run the sample once and read the status endpoint:

```json
{
  "ledgerSideEffects": 5,
  "reconciledReservations": 5,
  "notificationsReceivedByFakeApi": 1
}
```

Five side effects for six delivered records: reservation 101 arrived twice and was applied once,
and reservation 104 was dead-lettered instead of applied.

Now stop the process (Ctrl+C) and run it again **without deleting `reservation-reconciliation.db`**:

```json
{
  "ledgerSideEffects": 0,
  "reconciledReservations": 0
}
```

Zero. The in-memory ledger is empty because the process is new, but every reservation's
idempotency record survived in SQLite, so none of them produced a second side effect. That is the
at-least-once + idempotency contract working across a restart.

To start over, delete the database file:

```bash
rm samples/ReservationReconciliation.Worker/reservation-reconciliation.db
```

## Failure isolation

While `reservation-sync` retries against a failing page, `notification-dispatch` keeps completing
on its own schedule and the host stays up. The status endpoint shows both jobs' independent
`lastResult` and `consecutiveFailures` values.

## Notes

- The database is created with `EnsureCreated` (`AutoCreateSchema = true`) because this is a demo.
  Production deployments should own the schema via EF Core migrations — see
  [docs/persistence.md](../../docs/persistence.md).
- There are no secrets in this sample: the fake API needs no credentials, and the connection
  string points at a local file.
- The domain (reservations, room codes, nights) is fictional and exists only to make the
  reliability behavior concrete.
