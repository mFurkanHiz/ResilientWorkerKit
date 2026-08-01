namespace ResilientWorkerKit;

/// <summary>
/// Thrown by job code to explicitly mark a failure as permanent: the execution fails
/// immediately without retries (validation errors, domain rule violations, unsupported payloads).
/// </summary>
public class PermanentJobException : Exception, IJobFailureHint
{
    /// <summary>Creates the exception.</summary>
    public PermanentJobException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public JobFailureKind FailureKind => JobFailureKind.Permanent;

    /// <inheritdoc />
    public TimeSpan? RetryAfter => null;
}
