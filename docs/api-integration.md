# API Integration

`ResilientWorkerKit.Http` is the package a job uses to talk to an external HTTP API without
re-implementing retries, timeouts, correlation, idempotency and secret-safe logging in every
worker. It contains no job-engine code: it is an opinionated `IHttpClientFactory` pipeline plus a
handful of helpers that make failures classify correctly once they reach the engine.

## Registration

```csharp
services.AddResilientApiClient<IReservationApiClient, ReservationApiClient>("reservations", options =>
{
    options.BaseAddress      = new Uri(builder.Configuration["ReservationApi:BaseUrl"]!);
    options.AttemptTimeout   = TimeSpan.FromSeconds(5);
    options.TotalTimeout     = TimeSpan.FromSeconds(30);

    options.EnableCorrelationId  = true;              // default
    options.EnableIdempotencyKey = true;              // POST/PUT/PATCH
    options.ApiKeyHeaderName     = "X-Api-Key";       // value comes from IApiKeyProvider
    options.UseBearerTokenProvider = false;

    options.AdditionalMaskedHeaders.Add("X-Tenant-Signature");

    options.ConfigureResilience = resilience =>
    {
        resilience.Retry.MaxRetryAttempts = 4;
        resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
    };
});

// The API key never appears in source or in the options object.
services.AddSingleton<IApiKeyProvider>(sp =>
    new ConfigurationApiKeyProvider(sp.GetRequiredService<IConfiguration>()));
```

`AddResilientApiClient<TClient, TImplementation>` returns the `IHttpClientBuilder`, so anything
else `IHttpClientFactory` supports (extra handlers, primary handler configuration) can be chained
onto it.

Two things happen at registration time that are worth knowing:

- **Validation.** `TotalTimeout < AttemptTimeout` throws `ArgumentException` immediately — a
  configuration that could never complete a single attempt fails at startup, not in production.
- **`HttpClient.Timeout` is set to `Timeout.InfiniteTimeSpan`.** The resilience pipeline owns
  timeouts. `HttpClient.Timeout` applies to the *whole* call including retries and would race with
  the pipeline, cancelling mid-retry with a `TaskCanceledException` that carries no useful
  classification. Do not set it back.

The typed client itself stays trivial — see
`samples/ReservationReconciliation.Worker/Api/ReservationApiClient.cs`. It does happy-path I/O and
calls `EnsureApiSuccessAsync`; everything else is pipeline.

## The handler pipeline

Handlers are listed outermost (closest to your typed client) to innermost (closest to the socket):

| # | Handler | Condition | Runs |
|---|---|---|---|
| 1 | `CorrelationIdHandler` | `EnableCorrelationId` | once per logical request |
| 2 | `ApiKeyHandler` | `ApiKeyHeaderName` set | once per logical request |
| 3 | `BearerTokenHandler` | `UseBearerTokenProvider` | once per logical request |
| 4 | `IdempotencyKeyHandler` | `EnableIdempotencyKey` | once per logical request |
| 5 | **Standard resilience handler** | always | rate limiter → total timeout → retry → circuit breaker → attempt timeout |
| 6 | `SafeHttpLoggingHandler` | `EnableSafeLogging` | once per **physical attempt** |
| 7 | Primary handler (sockets) | always | once per physical attempt |

The order is not incidental:

- **The idempotency-key handler sits *outside* the resilience handler.** The key is stamped on the
  `HttpRequestMessage` before the pipeline ever sends it, so every retried attempt of that request
  carries *the same* `Idempotency-Key`. A server that deduplicates on that header therefore sees
  one logical operation even when the network forced four physical POSTs. If the handler were
  inside the pipeline, each retry would mint a fresh key and retries would become duplicate side
  effects — the exact failure mode the header exists to prevent.
- **The correlation and auth handlers sit outside for the same reason**: one correlation id per
  logical request keeps a retry storm greppable as a single operation, and the token is acquired
  once instead of once per attempt.
- **The safe logging handler is innermost.** It is the only place that sees every *physical*
  attempt, so its status codes and durations describe real network calls. A logging handler placed
  outside the pipeline would log one line per logical request and hide the retries completely.

## `ResilientApiClientOptions`

| Option | Type | Default | Meaning |
|---|---|---|---|
| `BaseAddress` | `Uri?` | `null` | Applied to `HttpClient.BaseAddress` when set. |
| `AttemptTimeout` | `TimeSpan` | 10 s | Timeout for one physical attempt (`resilience.AttemptTimeout.Timeout`). |
| `TotalTimeout` | `TimeSpan` | 30 s | Timeout across all attempts (`resilience.TotalRequestTimeout.Timeout`). Must be ≥ `AttemptTimeout`. |
| `EnableCorrelationId` | `bool` | `true` | Adds the correlation header when the request does not already carry one. |
| `CorrelationIdHeaderName` | `string` | `X-Correlation-ID` | Header name used for correlation. |
| `EnableIdempotencyKey` | `bool` | `false` | Adds an idempotency header to POST/PUT/PATCH requests that lack one. |
| `IdempotencyKeyHeaderName` | `string` | `Idempotency-Key` | Header name used for the idempotency key. |
| `ApiKeyHeaderName` | `string?` | `null` | When set, registers `ApiKeyHandler` and sends the value from `IApiKeyProvider` in this header. |
| `UseBearerTokenProvider` | `bool` | `false` | When true, registers `BearerTokenHandler` and resolves `IBearerTokenProvider`. |
| `EnableSafeLogging` | `bool` | `true` | Registers `SafeHttpLoggingHandler` under the category `ResilientWorkerKit.Http.{name}`. |
| `AdditionalMaskedHeaders` | `ISet<string>` | empty | Extra header names your own diagnostics should treat as sensitive (see [Logging and masking](#logging-and-masking)). |
| `ConfigureResilience` | `Action<HttpStandardResilienceOptions>?` | `null` | Runs after the kit has applied the two timeouts, so it can override anything including them. |

`ApiKeyHandler` and `BearerTokenHandler` resolve their providers with `GetRequiredService`, so the
corresponding provider must be registered or the first request throws.

## Authentication

Two contracts, both async, both intended to read from configuration or a secret store:

```csharp
public interface IApiKeyProvider
{
    ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken = default);
}

public interface IBearerTokenProvider
{
    ValueTask<BearerToken> GetTokenAsync(CancellationToken cancellationToken = default);
}

public sealed record BearerToken(string AccessToken, DateTimeOffset? ExpiresAtUtc);
```

`ApiKeyHandler` sends the key in `ApiKeyHeaderName`, but only when the request does not already
contain that header — an explicit per-request key wins.

`BearerTokenHandler` sets `Authorization: Bearer <token>` on every request (it overwrites any
existing value) and, when the response is `401 Unauthorized` **and** the provider is a
`CachingBearerTokenProvider`, calls `Invalidate()` so the next request acquires a fresh token.
Because the handler is outside the resilience pipeline, the 401 it observes is the pipeline's final
response, and the refresh benefits the *next* logical request — there is no automatic in-request
retry after a 401.

### `CachingBearerTokenProvider`

A decorator, not a base class. Wrap your real provider:

```csharp
services.AddSingleton<IBearerTokenProvider>(sp =>
    new CachingBearerTokenProvider(new ClientCredentialsTokenProvider(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IConfiguration>())));
```

Behavior:

- A cached token is reused while `ExpiresAtUtc - 30 s > now`. The 30-second **expiry skew** is
  fixed and exists so a token cannot expire in flight between the header being written and the
  server validating it.
- A token with `ExpiresAtUtc == null` is cached indefinitely (until `Invalidate()`).
- Acquisition is guarded by a semaphore with double-checked reads, so a burst of concurrent
  requests triggers exactly one call to the inner provider.
- `Invalidate()` drops the cached token; it is called automatically on 401 by `BearerTokenHandler`.
- Time comes from an injectable `TimeProvider`, so expiry behavior is testable.

### The secrets rule

API keys, client secrets and tokens come from configuration, environment variables, user-secrets or
a secret store — **never from source code**, and never from a literal in the options object. That is
why `ResilientApiClientOptions` exposes only the *header name*: there is nowhere to put a key. The
same rule applies to anything the kit persists; see [persistence.md](persistence.md).

## Resilience

The pipeline is `AddStandardResilienceHandler` from `Microsoft.Extensions.Http.Resilience`, which
composes five strategies in this order:

| Strategy | What it does | Configured by the kit |
|---|---|---|
| Rate limiter | Bounds concurrent outbound requests for this client | no — library default |
| Total request timeout | Caps the whole request including all retries | yes — `TotalTimeout` |
| Retry | Exponential backoff **with jitter**, honoring `Retry-After` on the response | no — library default |
| Circuit breaker | Opens after a sustained failure ratio, short-circuiting further calls | no — library default |
| Attempt timeout | Caps one physical attempt | yes — `AttemptTimeout` |

The kit assigns `resilience.AttemptTimeout.Timeout` and `resilience.TotalRequestTimeout.Timeout`
from the options and then invokes `ConfigureResilience`, so your callback can override those two as
well as everything the kit did not touch:

```csharp
options.ConfigureResilience = resilience =>
{
    resilience.Retry.MaxRetryAttempts = 5;
    resilience.Retry.Delay            = TimeSpan.FromMilliseconds(500);
    resilience.CircuitBreaker.FailureRatio     = 0.25;
    resilience.CircuitBreaker.BreakDuration    = TimeSpan.FromSeconds(15);
    resilience.RateLimiter.DefaultRateLimiterOptions.PermitLimit = 20;
};
```

Because the pipeline owns timeouts, `HttpClient.Timeout` is `Timeout.InfiniteTimeSpan` for every
client registered this way (see [Registration](#registration)).

## Two layers of retry

There are two independent retry loops, and conflating them is the most common way to build a worker
that hammers an API for twenty minutes:

| | HTTP pipeline retry | Job engine retry |
|---|---|---|
| Scope | One `HttpClient` call | The whole `ExecuteAsync` body |
| Configured by | `ConfigureResilience` | `job.WithRetry(...)` / `WithRetryCount(...)` |
| Visible in execution history | no | yes — `AttemptCount` |
| Preserves in-memory progress | yes (the job never noticed) | no — the body restarts from its checkpoint |
| Bounded by | `TotalTimeout` per request | `WithTimeout(...)` per execution |

The multiplication is real: `MaxRetryAttempts = 3` in the pipeline and `MaxRetries = 3` on the job
means one bad occurrence can produce up to sixteen physical requests. Choose deliberately:

- **Keep HTTP retry on** for chatty jobs where a single blip in the middle of a long pagination
  sweep should not throw away the progress made so far. This is the default and it is usually right.
- **Turn HTTP retry off** when you want every attempt to be visible in the execution record — for
  audit trails, for jobs whose runs are expensive, or when the job's own checkpoint-and-resume logic
  is the honest unit of retry. `MaxRetryAttempts` cannot be set to zero (the library validates it as
  ≥ 1), so disable the strategy through its predicate:

```csharp
options.ConfigureResilience = resilience =>
{
    // Nothing is retried at the HTTP layer; every failure surfaces to the job engine,
    // which records it as an attempt on the execution.
    resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
};
```

Whichever layer retries, the engine still needs to know whether the failure was worth retrying —
that is what `ApiRequestException` is for.

## Response validation

```csharp
using var response = await _httpClient.GetAsync(uri, cancellationToken);
await response.EnsureApiSuccessAsync(cancellationToken);
```

`EnsureApiSuccessAsync` returns immediately for success status codes. Otherwise it throws
`ApiRequestException` whose message contains exactly four things:

1. the HTTP method,
2. `scheme://authority` plus the **absolute path** of the request URI,
3. the numeric status code and reason phrase,
4. when the response body is `application/problem+json` or `application/json`, is non-empty and is
   at most 8,192 characters long, the ProblemDetails `title` — itself truncated to 200 characters.
   A body that is not valid JSON, is not a JSON object, or carries no string `title` simply
   contributes nothing.

The whole message is then run through `SensitiveDataMasker.MaskSecrets`.

It deliberately never contains:

- **the query string** — API keys, tokens and record identifiers routinely ride in query
  parameters, and an exception message ends up in logs and in the `WorkerKitExecutions` table;
- **the response body** (beyond the single `title` field) — bodies carry personal data;
- **request headers or credentials** of any kind.

`HttpUnitTests.Message_ContainsProblemTitle_ButNeverTheQueryString` pins this: a request to
`https://api.example.test/reservations?apiKey=should-not-leak` produces a message containing
`/reservations` and the problem title, and containing neither `apiKey` nor its value.

### `ParseRetryAfter`

```csharp
TimeSpan? delay = response.ParseRetryAfter();
```

Reads `HttpResponseMessage.Headers.RetryAfter` and handles both RFC forms:

- **delta-seconds** (`Retry-After: 17`) → that `TimeSpan`;
- **HTTP-date** (`Retry-After: Fri, 01 Aug 2026 10:00:00 GMT`) → the remaining time until that
  instant, computed against `DateTimeOffset.UtcNow`.

Negative results (a date already in the past, a negative delta) are clamped to `TimeSpan.Zero`; a
missing or unparsable header yields `null`. `EnsureApiSuccessAsync` calls this automatically and
puts the result on the exception.

## `ApiRequestException` and failure classification

`ApiRequestException` implements `IJobFailureHint`, which the engine's default classifier consults
before anything else. That means an HTTP status code decides — deterministically — whether the job
engine retries the execution:

| Status | `JobFailureKind` | Engine behavior |
|---|---|---|
| no status (`StatusCode == null`) | `Transient` | retried |
| 408 Request Timeout | `Transient` | retried |
| 429 Too Many Requests | `Transient` | retried, honoring `RetryAfter` |
| ≥ 500 | `Transient` | retried |
| other 4xx (400–499) | `Permanent` | **not** retried; execution fails immediately |
| anything else (1xx/2xx/3xx reaching this path) | `Transient` | retried |

The `RetryAfter` on the exception overrides the engine's computed backoff for that attempt (see
[execution-semantics.md](execution-semantics.md#retry)). So a 429 with `Retry-After: 60` makes the
job wait a minute rather than its configured two seconds — without any code in the job.

`ApiRequestException` is not sealed; derive from it if you need to carry extra safe metadata.

## Correlation and idempotency propagation

`CorrelationIdHandler` resolves the value in this order and adds the header only if the request does
not already contain it:

1. an explicit value set via `request.WithCorrelationId(...)`;
2. `Activity.Current?.TraceId` — so an ambient trace ties the outbound call to the execution span;
3. a new `Guid.NewGuid().ToString("n")`.

`IdempotencyKeyHandler` acts only on POST, PUT and PATCH, only when `EnableIdempotencyKey` is true,
and only when the header is absent. It uses an explicit `request.WithIdempotencyKey(...)` value if
one was set, otherwise a fresh GUID.

Both request extensions write into `HttpRequestMessage.Options` under typed keys exposed as
`ResilientHttpRequestOptions.CorrelationId` / `.IdempotencyKey`:

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "notifications")
{
    Content = JsonContent.Create(payload),
};

// A *stable business identity*, not a random GUID: the same logical notification
// gets the same key across job retries, host restarts and redeployments.
request.WithIdempotencyKey($"notification:{context.ExecutionId}")
       .WithCorrelationId(context.CorrelationId);

using var response = await _httpClient.SendAsync(request, cancellationToken);
await response.EnsureApiSuccessAsync(cancellationToken);
```

The auto-generated GUID only deduplicates the *pipeline's own* retries, because it is minted once
per `HttpRequestMessage`. To also deduplicate across job-level retries and restarts, pass an
explicit key derived from business identity (`entity:id:version`), exactly as the job's local
`context.Idempotency` keys are derived. The engine's correlation id (`context.CorrelationId`, which
equals the `ExecutionId`) is the natural value for `WithCorrelationId`.

## Pagination

```csharp
public sealed record ContinuationPage<T>(IReadOnlyList<T> Items, string? NextContinuationToken);
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
```

Both are plain page records: items plus a token that is `null` or empty on the last page. Your typed
client maps the API's own shape onto one of them.

`PageReader` drains them:

```csharp
await foreach (var reservation in PageReader.ReadAllAsync<Reservation>(
    (token, ct) => _apiClient.GetReservationsAsync(token, ct),
    cancellationToken))
{
    // one item at a time; pages are fetched lazily
}

// Cursor-shaped APIs use the sibling overload, which has identical semantics.
await foreach (var item in PageReader.ReadAllByCursorAsync<Item>(
    (cursor, ct) => _apiClient.GetItemsAsync(cursor, ct), cancellationToken))
```

Semantics:

- The first call receives `null` as the token.
- Items are yielded in page order; the cancellation token is checked before each fetch.
- Enumeration stops when the returned token is `null` or empty.
- **Non-advancing-token guard**: if a page returns a token *equal to the one that produced it*,
  `ReadAllAsync` throws `InvalidOperationException` instead of looping forever. This is a common
  server-side bug, and without the guard a worker would spin on one page indefinitely, burning rate
  limit and never failing loudly. `PageReaderTests.NonAdvancingToken_Throws_InsteadOfLoopingForever`
  covers it.

`ReadAllByCursorAsync` delegates to `ReadAllAsync`, so it inherits the guard.

Note that `PageReader` streams: it does not buffer the whole result set. Combined with
`context.Checkpoints`, that is what lets a sync job resume mid-sweep — see
`ReservationSyncJob` in the reservation sample, which checkpoints the continuation token only after
a page has been fully processed.

## Logging and masking

When `EnableSafeLogging` is true (the default), `SafeHttpLoggingHandler` logs under the category
`ResilientWorkerKit.Http.{name}` — `name` being the client name passed to
`AddResilientApiClient`, so each client gets its own filterable category.

| Situation | Level | Fields |
|---|---|---|
| Success status | `Debug` | `Method`, `Target`, `StatusCode`, `ElapsedMs`, `CorrelationId` |
| Non-success status | `Warning` | same |
| Exception (not `OperationCanceledException`) | `Warning`, with the exception object | `Method`, `Target`, `ElapsedMs`, `CorrelationId` |

`Target` is `{scheme}://{authority}{absolute path}` — the same safe form
`EnsureApiSuccessAsync` uses, so **the query string is never logged**. `CorrelationId` is read back
off the outgoing request headers. `OperationCanceledException` is rethrown without a log entry:
shutdown and timeouts are not the HTTP layer's error to report. Request and response bodies are
never read or logged, and no headers are logged at all.

### `SensitiveDataMasker`

A static helper used by the pipeline and available to your own diagnostics.

`IsSensitiveHeader(headerName, additional?)` — case-insensitive membership test over this built-in
list:

`Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, `Api-Key`,
`X-Auth-Token`, `X-Amz-Security-Token`.

The optional `additional` argument is where `options.AdditionalMaskedHeaders` belongs. Note that the
built-in logging handler logs no headers at all, so `AdditionalMaskedHeaders` does not change what
the pipeline emits — it exists so application code that *does* dump headers can ask one question
(`SensitiveDataMasker.IsSensitiveHeader(name, options.AdditionalMaskedHeaders)`) and get a
consistent answer.

`MaskSecrets(input)` replaces two patterns with `***`:

- **Auth schemes**: `Bearer <token>` / `Basic <credentials>` (case-insensitive) → `Bearer ***`.
- **Key/value secrets**: `api_key`, `api-key`, `apikey`, `access_token`, `refresh_token`,
  `client_secret`, `password`, `secret`, `token` followed by `=` or `:` and a value → the key and
  separator are kept, the value becomes `***`.

Plain text without those patterns is returned unchanged. Use it on any free-text string that might
have come from an upstream system before you log it or persist it:

```csharp
context.Logger.LogWarning("Upstream rejected the batch: {Detail}",
    SensitiveDataMasker.MaskSecrets(detail));
```

The masker is a defense in depth, not a licence to log secrets: it recognizes common shapes, not all
of them.
