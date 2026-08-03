# ResilientWorkerKit.Http

HTTP integration for [ResilientWorkerKit](https://www.nuget.org/packages/ResilientWorkerKit)
jobs: typed `HttpClient` registration built on `IHttpClientFactory` and
`Microsoft.Extensions.Http.Resilience`.

## What it adds

- **Resilience:** retry with backoff and jitter honouring `Retry-After`, circuit breaker,
  rate limiter, and attempt/total timeouts — one pipeline, configured per client.
- **Correlation:** the job's `CorrelationId` is propagated as `X-Correlation-ID`, so one
  execution is traceable across your logs and the upstream API's.
- **Idempotency keys:** the key handler sits *outside* the retry handler, so every retry of a
  request reuses the same `Idempotency-Key` — which is the point of having one.
- **Auth:** API-key (`IApiKeyProvider`) and bearer (`IBearerTokenProvider`) handlers with
  token caching and refresh-on-401.
- **Safe errors:** `EnsureApiSuccessAsync` produces errors that never contain query strings or
  response bodies, and `ApiRequestException` feeds the engine's retry classification
  (transient vs permanent) instead of guessing from strings.
- Pagination helpers, and request logging that masks secrets by construction.

## Quick start

```csharp
builder.Services.AddResilientApiClient<IReservationsApi, ReservationsApiClient>(
    "reservations",
    options =>
    {
        options.BaseAddress = new Uri("https://api.example.com/");
        options.ApiKeyHeaderName = "X-Api-Key";   // resolved via your IApiKeyProvider
    });
```

```csharp
var response = await _api.GetUpdatedReservationsAsync(cursor, ct);
await response.EnsureApiSuccessAsync(ct); // throws a classified, safe ApiRequestException
```

## Links

[Repository](https://github.com/mFurkanHiz/ResilientWorkerKit) ·
[API integration guide](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/docs/api-integration.md) ·
[Changelog](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/CHANGELOG.md) ·
MIT licensed · `net10.0` and `net8.0`
