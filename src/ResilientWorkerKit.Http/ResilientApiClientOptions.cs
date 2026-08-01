using Microsoft.Extensions.Http.Resilience;

namespace ResilientWorkerKit.Http;

/// <summary>Configuration for one resilient typed API client.</summary>
public sealed class ResilientApiClientOptions
{
    /// <summary>Base address of the API.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Timeout for one attempt (the resilience pipeline retries within the total). Default 10 s.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Total timeout across all attempts. Default 30 s.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Adds a correlation-id header to every request. Default true.</summary>
    public bool EnableCorrelationId { get; set; } = true;

    /// <summary>The correlation-id header name. Default <c>X-Correlation-ID</c>.</summary>
    public string CorrelationIdHeaderName { get; set; } = "X-Correlation-ID";

    /// <summary>
    /// Adds an <c>Idempotency-Key</c> header to POST/PUT/PATCH requests that lack one, so
    /// server-side deduplication can suppress replays caused by retries. Default false.
    /// </summary>
    public bool EnableIdempotencyKey { get; set; }

    /// <summary>The idempotency-key header name. Default <c>Idempotency-Key</c>.</summary>
    public string IdempotencyKeyHeaderName { get; set; } = "Idempotency-Key";

    /// <summary>
    /// When set, an API key from the registered <see cref="IApiKeyProvider"/> is sent in this
    /// header. The key value itself never appears in configuration objects or logs.
    /// </summary>
    public string? ApiKeyHeaderName { get; set; }

    /// <summary>
    /// Attaches bearer tokens from the registered <see cref="IBearerTokenProvider"/>.
    /// Wrap the provider in <see cref="CachingBearerTokenProvider"/> to avoid per-request
    /// token acquisition. Default false.
    /// </summary>
    public bool UseBearerTokenProvider { get; set; }

    /// <summary>Logs safe request metadata (method, host, path, status, duration — never bodies, queries or secrets). Default true.</summary>
    public bool EnableSafeLogging { get; set; } = true;

    /// <summary>Extra header names to treat as sensitive in diagnostics.</summary>
    public ISet<string> AdditionalMaskedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Customizes the standard resilience pipeline (retry with backoff+jitter honoring
    /// <c>Retry-After</c>, circuit breaker, rate limiter, timeouts).
    /// </summary>
    public Action<HttpStandardResilienceOptions>? ConfigureResilience { get; set; }
}
