namespace ResilientWorkerKit;

/// <summary>The validated, immutable set of jobs registered with the host.</summary>
public interface IJobRegistry
{
    /// <summary>All registered job definitions.</summary>
    IReadOnlyList<JobDefinition> Jobs { get; }

    /// <summary>Returns the definition for the given job id, or null.</summary>
    JobDefinition? Find(string jobId);
}
