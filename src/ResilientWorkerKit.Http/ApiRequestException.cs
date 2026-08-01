using System.Net;

namespace ResilientWorkerKit.Http;

/// <summary>
/// A failed API response, carrying a safe message (no bodies, no query strings, no secrets)
/// and a failure hint so the ResilientWorkerKit retry engine classifies it correctly:
/// 408/429/5xx (and unknown) → transient, other 4xx → permanent, with <c>Retry-After</c>
/// propagated into the backoff.
/// </summary>
public class ApiRequestException : Exception, IJobFailureHint
{
    /// <summary>Creates the exception.</summary>
    public ApiRequestException(string message, HttpStatusCode? statusCode, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        FailureKind = Classify(statusCode);
    }

    /// <summary>The HTTP status code, when a response was received.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <inheritdoc />
    public JobFailureKind FailureKind { get; }

    /// <inheritdoc />
    public TimeSpan? RetryAfter { get; }

    private static JobFailureKind Classify(HttpStatusCode? statusCode) => (int?)statusCode switch
    {
        null => JobFailureKind.Transient,
        408 or 429 or >= 500 => JobFailureKind.Transient,
        >= 400 and < 500 => JobFailureKind.Permanent,
        _ => JobFailureKind.Transient,
    };
}
