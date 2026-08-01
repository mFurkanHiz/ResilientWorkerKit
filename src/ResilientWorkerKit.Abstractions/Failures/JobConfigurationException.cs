namespace ResilientWorkerKit;

/// <summary>
/// Signals invalid job configuration (unknown time zone, day-of-month out of range, duplicate
/// job ids, corrupted checkpoint payloads...). Thrown during startup validation where possible;
/// classified as <see cref="JobFailureKind.Misconfigured"/> (never retried) when it surfaces at runtime.
/// </summary>
public class JobConfigurationException : Exception, IJobFailureHint
{
    /// <summary>Creates the exception.</summary>
    public JobConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public JobFailureKind FailureKind => JobFailureKind.Misconfigured;

    /// <inheritdoc />
    public TimeSpan? RetryAfter => null;
}
