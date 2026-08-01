namespace ResilientWorkerKit.Engine;

/// <summary>Default manual trigger implementation routing requests into the job's schedule loop.</summary>
internal sealed class ManualJobTrigger : IManualJobTrigger
{
    private readonly WorkerKitHostedService _host;
    private readonly IJobRegistry _registry;

    public ManualJobTrigger(WorkerKitHostedService host, IJobRegistry registry)
    {
        _host = host;
        _registry = registry;
    }

    /// <inheritdoc />
    public async Task<string> TriggerAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var definition = _registry.Find(jobId)
            ?? throw new JobConfigurationException($"Unknown job id '{jobId}'.");
        if (!definition.Enabled)
        {
            throw new JobConfigurationException($"Job '{jobId}' is disabled and cannot be triggered.");
        }

        var request = new ManualTriggerRequest(
            Guid.NewGuid().ToString("n"),
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

        if (!_host.TryEnqueueManualTrigger(jobId, request))
        {
            throw new JobConfigurationException($"Job '{jobId}' has no active schedule loop.");
        }

        return await request.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
