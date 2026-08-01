namespace ResilientWorkerKit.Registration;

/// <summary>Immutable, validated job registry.</summary>
internal sealed class JobRegistry : IJobRegistry
{
    private readonly Dictionary<string, JobDefinition> _byId;

    public JobRegistry(IReadOnlyList<JobDefinition> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _byId = new Dictionary<string, JobDefinition>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (!_byId.TryAdd(job.JobId, job))
            {
                throw new JobConfigurationException(
                    $"Duplicate job id '{job.JobId}'. Every job needs a unique id; pass an explicit id to AddJob.");
            }
        }

        Jobs = jobs;
    }

    public IReadOnlyList<JobDefinition> Jobs { get; }

    public JobDefinition? Find(string jobId)
        => _byId.TryGetValue(jobId, out var definition) ? definition : null;
}
