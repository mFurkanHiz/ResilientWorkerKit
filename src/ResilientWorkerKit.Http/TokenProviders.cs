namespace ResilientWorkerKit.Http;

/// <summary>A bearer token with optional expiry.</summary>
/// <param name="AccessToken">The raw token value. Never log it.</param>
/// <param name="ExpiresAtUtc">Expiry, when known; enables caching.</param>
public sealed record BearerToken(string AccessToken, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// Supplies bearer tokens for outbound API calls (client-credentials flows, token endpoints...).
/// Implementations should not cache — register <see cref="CachingBearerTokenProvider"/> as the
/// decorator instead.
/// </summary>
public interface IBearerTokenProvider
{
    /// <summary>Returns a valid token.</summary>
    ValueTask<BearerToken> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Supplies the API key value for <see cref="ResilientApiClientOptions.ApiKeyHeaderName"/>. Load it from configuration/secret stores — never from source code.</summary>
public interface IApiKeyProvider
{
    /// <summary>Returns the API key.</summary>
    ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Caches tokens from an inner <see cref="IBearerTokenProvider"/> until shortly before expiry,
/// eliminating per-request (or per-cycle) token endpoint traffic. Invalidated automatically
/// when a request comes back 401.
/// </summary>
public sealed class CachingBearerTokenProvider : IBearerTokenProvider, IDisposable
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private readonly IBearerTokenProvider _inner;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private BearerToken? _cached;

    /// <summary>Creates the caching decorator.</summary>
    public CachingBearerTokenProvider(IBearerTokenProvider inner, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<BearerToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (IsUsable(cached))
        {
            return cached!;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cached);
            if (IsUsable(cached))
            {
                return cached!;
            }

            var fresh = await _inner.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cached, fresh);
            return fresh;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Drops the cached token (called on 401 responses).</summary>
    public void Invalidate() => Volatile.Write(ref _cached, null);

    /// <inheritdoc />
    public void Dispose() => _mutex.Dispose();

    private bool IsUsable(BearerToken? token)
        => token is not null
           && (token.ExpiresAtUtc is not { } expiry || expiry - ExpirySkew > _time.GetUtcNow());
}
