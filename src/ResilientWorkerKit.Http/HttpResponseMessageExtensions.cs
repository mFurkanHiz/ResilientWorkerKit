using System.Text.Json;

namespace ResilientWorkerKit.Http;

/// <summary>Safe response validation helpers.</summary>
public static class HttpResponseMessageExtensions
{
    private const int MaxErrorBodyBytes = 8 * 1024;

    /// <summary>
    /// Throws <see cref="ApiRequestException"/> for non-success responses with a safe message:
    /// method, authority, path (no query), status, and — when the body is a JSON problem
    /// document — its masked <c>title</c>. <c>Retry-After</c> is parsed and propagated to the
    /// retry engine.
    /// </summary>
    public static async Task EnsureApiSuccessAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var request = response.RequestMessage;
        var method = request?.Method.Method ?? "?";
        var uri = request?.RequestUri;
        var safeTarget = uri is null ? "?" : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";

        var retryAfter = ParseRetryAfter(response);
        var title = await TryReadProblemTitleAsync(response, cancellationToken).ConfigureAwait(false);

        var message = $"API request {method} {safeTarget} failed with {(int)response.StatusCode} {response.ReasonPhrase}."
            + (title is null ? string.Empty : $" {title}");

        throw new ApiRequestException(SensitiveDataMasker.MaskSecrets(message), response.StatusCode, retryAfter);
    }

    /// <summary>Parses the <c>Retry-After</c> header (delta or absolute date form), if present.</summary>
    public static TimeSpan? ParseRetryAfter(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static async Task<string?> TryReadProblemTitleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not ("application/problem+json" or "application/json"))
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body) || body.Length > MaxErrorBodyBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (document.RootElement.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String
                && titleElement.GetString() is { Length: > 0 } title)
            {
                return title.Length > 200 ? title[..200] : title;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
