namespace ResilientWorkerKit;

/// <summary>Result of classifying an exception thrown by a job execution.</summary>
/// <param name="Kind">The failure category.</param>
/// <param name="RetryAfter">
/// Optional delay before the next retry attempt (e.g. parsed from an HTTP <c>Retry-After</c>
/// header). When present it replaces the computed backoff entirely, whether it is longer or
/// shorter, because the server's instruction is the better information.
/// </param>
public readonly record struct JobFailureClassification(JobFailureKind Kind, TimeSpan? RetryAfter = null)
{
    /// <summary>A transient failure with no retry-after hint.</summary>
    public static JobFailureClassification Transient { get; } = new(JobFailureKind.Transient);

    /// <summary>A permanent failure.</summary>
    public static JobFailureClassification Permanent { get; } = new(JobFailureKind.Permanent);
}
