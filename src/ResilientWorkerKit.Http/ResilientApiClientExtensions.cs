using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit.Http;

/// <summary>Registration of resilient typed API clients.</summary>
public static class ResilientApiClientExtensions
{
    /// <summary>
    /// Registers a typed HttpClient with the full ResilientWorkerKit HTTP pipeline:
    /// correlation-id and idempotency-key propagation, optional API-key/bearer auth handlers,
    /// the standard resilience pipeline (retry with backoff+jitter honoring
    /// <c>Retry-After</c>, circuit breaker, rate limiter, attempt/total timeouts) and safe
    /// masked logging of request metadata.
    /// </summary>
    public static IHttpClientBuilder AddResilientApiClient<TClient, TImplementation>(
        this IServiceCollection services,
        string name,
        Action<ResilientApiClientOptions> configure)
        where TClient : class
        where TImplementation : class, TClient
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ResilientApiClientOptions();
        configure(options);

        if (options.TotalTimeout < options.AttemptTimeout)
        {
            throw new ArgumentException(
                $"TotalTimeout ({options.TotalTimeout}) must be greater than or equal to AttemptTimeout ({options.AttemptTimeout}).",
                nameof(configure));
        }

        var httpBuilder = services.AddHttpClient<TClient, TImplementation>(name, client =>
        {
            if (options.BaseAddress is not null)
            {
                client.BaseAddress = options.BaseAddress;
            }

            // The resilience pipeline owns timeouts; HttpClient.Timeout would race with it.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        httpBuilder.AddHttpMessageHandler(() => new CorrelationIdHandler(options));

        if (options.ApiKeyHeaderName is { } apiKeyHeader)
        {
            httpBuilder.AddHttpMessageHandler(sp =>
                new ApiKeyHandler(apiKeyHeader, sp.GetRequiredService<IApiKeyProvider>()));
        }

        if (options.UseBearerTokenProvider)
        {
            httpBuilder.AddHttpMessageHandler(sp =>
                new BearerTokenHandler(sp.GetRequiredService<IBearerTokenProvider>()));
        }

        // Outside the resilience handler on purpose: retried attempts reuse the same key.
        httpBuilder.AddHttpMessageHandler(() => new IdempotencyKeyHandler(options));

        httpBuilder.AddStandardResilienceHandler(resilience =>
        {
            resilience.AttemptTimeout.Timeout = options.AttemptTimeout;
            resilience.TotalRequestTimeout.Timeout = options.TotalTimeout;
            options.ConfigureResilience?.Invoke(resilience);
        });

        if (options.EnableSafeLogging)
        {
            // Innermost handler: logs every physical attempt the resilience pipeline makes.
            httpBuilder.AddHttpMessageHandler(sp => new SafeHttpLoggingHandler(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ResilientWorkerKit.Http.{name}"),
                options));
        }

        return httpBuilder;
    }
}
