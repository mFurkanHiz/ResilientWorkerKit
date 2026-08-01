namespace ResilientWorkerKit;

/// <summary>
/// Implemented by exception types that already know how they should be classified.
/// The default <see cref="IJobFailureClassifier"/> honors this hint before applying any
/// built-in heuristics, which lets other packages (e.g. ResilientWorkerKit.Http) influence
/// retry behavior without the core depending on them.
/// </summary>
public interface IJobFailureHint
{
    /// <summary>The failure category this exception represents.</summary>
    JobFailureKind FailureKind { get; }

    /// <summary>
    /// Optional delay before the next retry attempt. When present it replaces the computed
    /// backoff entirely (see <see cref="JobFailureClassification"/>).
    /// </summary>
    TimeSpan? RetryAfter { get; }
}
