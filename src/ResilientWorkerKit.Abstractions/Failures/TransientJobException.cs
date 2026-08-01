namespace ResilientWorkerKit;

/// <summary>
/// Thrown by job code to explicitly mark a failure as transient (retry-eligible),
/// optionally with a minimum delay before the next attempt.
/// </summary>
public class TransientJobException : Exception, IJobFailureHint
{
    /// <summary>Creates the exception.</summary>
    public TransientJobException(string message, Exception? innerException = null, TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    /// <inheritdoc />
    public JobFailureKind FailureKind => JobFailureKind.Transient;

    /// <inheritdoc />
    public TimeSpan? RetryAfter { get; }
}
