namespace ResilientWorkerKit;

/// <summary>
/// Maps exceptions thrown by job code to a <see cref="JobFailureClassification"/> that drives
/// the retry decision. Replace or decorate the default implementation to teach the engine about
/// domain-specific exception types.
/// </summary>
public interface IJobFailureClassifier
{
    /// <summary>Classifies the given exception.</summary>
    /// <param name="exception">The exception thrown by an execution attempt.</param>
    JobFailureClassification Classify(Exception exception);
}
