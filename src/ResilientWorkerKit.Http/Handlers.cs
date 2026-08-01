using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit.Http;

/// <summary>Typed <see cref="HttpRequestMessage.Options"/> keys and setters.</summary>
public static class ResilientHttpRequestOptions
{
    /// <summary>Explicit correlation id for a request.</summary>
    public static readonly HttpRequestOptionsKey<string> CorrelationId = new("workerkit.correlation-id");

    /// <summary>Explicit idempotency key for a request.</summary>
    public static readonly HttpRequestOptionsKey<string> IdempotencyKey = new("workerkit.idempotency-key");

    /// <summary>Sets an explicit correlation id (otherwise the trace id or a new GUID is used).</summary>
    public static HttpRequestMessage WithCorrelationId(this HttpRequestMessage request, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(CorrelationId, correlationId);
        return request;
    }

    /// <summary>Sets an explicit idempotency key (e.g. a stable business identity) for the request.</summary>
    public static HttpRequestMessage WithIdempotencyKey(this HttpRequestMessage request, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(IdempotencyKey, idempotencyKey);
        return request;
    }
}

/// <summary>Adds a correlation-id header: explicit request option → current trace id → new GUID.</summary>
public sealed class CorrelationIdHandler : DelegatingHandler
{
    private readonly ResilientApiClientOptions _options;

    /// <summary>Creates the handler.</summary>
    public CorrelationIdHandler(ResilientApiClientOptions options) => _options = options;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.EnableCorrelationId && !request.Headers.Contains(_options.CorrelationIdHeaderName))
        {
            var correlationId =
                (request.Options.TryGetValue(ResilientHttpRequestOptions.CorrelationId, out var explicitId) ? explicitId : null)
                ?? Activity.Current?.TraceId.ToString()
                ?? Guid.NewGuid().ToString("n");
            request.Headers.TryAddWithoutValidation(_options.CorrelationIdHeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Adds an idempotency-key header to POST/PUT/PATCH requests lacking one. Sits *outside* the
/// resilience handler, so every retry of a request carries the same key and the server can
/// deduplicate replays.
/// </summary>
public sealed class IdempotencyKeyHandler : DelegatingHandler
{
    private readonly ResilientApiClientOptions _options;

    /// <summary>Creates the handler.</summary>
    public IdempotencyKeyHandler(ResilientApiClientOptions options) => _options = options;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.EnableIdempotencyKey
            && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put || request.Method == HttpMethod.Patch)
            && !request.Headers.Contains(_options.IdempotencyKeyHeaderName))
        {
            var key =
                (request.Options.TryGetValue(ResilientHttpRequestOptions.IdempotencyKey, out var explicitKey) ? explicitKey : null)
                ?? Guid.NewGuid().ToString("n");
            request.Headers.TryAddWithoutValidation(_options.IdempotencyKeyHeaderName, key);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Sends the API key from <see cref="IApiKeyProvider"/> in the configured header.</summary>
public sealed class ApiKeyHandler : DelegatingHandler
{
    private readonly string _headerName;
    private readonly IApiKeyProvider _provider;

    /// <summary>Creates the handler.</summary>
    public ApiKeyHandler(string headerName, IApiKeyProvider provider)
    {
        _headerName = headerName;
        _provider = provider;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(_headerName))
        {
            var apiKey = await _provider.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.TryAddWithoutValidation(_headerName, apiKey);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Attaches a bearer token from <see cref="IBearerTokenProvider"/>. A 401 response invalidates
/// a <see cref="CachingBearerTokenProvider"/> cache so the next request acquires a fresh token.
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly IBearerTokenProvider _provider;

    /// <summary>Creates the handler.</summary>
    public BearerTokenHandler(IBearerTokenProvider provider) => _provider = provider;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && _provider is CachingBearerTokenProvider caching)
        {
            caching.Invalidate();
        }

        return response;
    }
}

/// <summary>
/// Logs safe request metadata for every physical attempt: method, host, path (never the query
/// string), status code and duration. Bodies and sensitive headers are never logged; free-text
/// parts are run through <see cref="SensitiveDataMasker"/>.
/// </summary>
public sealed class SafeHttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly ResilientApiClientOptions _options;

    /// <summary>Creates the handler.</summary>
    public SafeHttpLoggingHandler(ILogger logger, ResilientApiClientOptions options)
    {
        _logger = logger;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var safeTarget = uri is null ? "?" : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        var correlationId = request.Headers.TryGetValues(_options.CorrelationIdHeaderName, out var values)
            ? values.FirstOrDefault()
            : null;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "HTTP {Method} {Target} -> {StatusCode} in {ElapsedMs} ms (correlation {CorrelationId})",
                    request.Method.Method, safeTarget, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, correlationId);
            }
            else
            {
                _logger.LogWarning(
                    "HTTP {Method} {Target} -> {StatusCode} in {ElapsedMs} ms (correlation {CorrelationId})",
                    request.Method.Method, safeTarget, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, correlationId);
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex,
                "HTTP {Method} {Target} threw after {ElapsedMs} ms (correlation {CorrelationId})",
                request.Method.Method, safeTarget, stopwatch.ElapsedMilliseconds, correlationId);
            throw;
        }
    }
}
