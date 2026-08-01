namespace ResilientWorkerKit;

/// <summary>Receives safe, short progress notes from running jobs (fed into health snapshots).</summary>
public interface IJobProgressReporter
{
    /// <summary>Reports a progress note for the given job execution.</summary>
    void Report(string jobId, string executionId, string message);
}
