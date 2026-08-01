using Microsoft.Extensions.DependencyInjection;

namespace ResilientWorkerKit;

/// <summary>Root configuration surface of <c>AddResilientWorkerKit</c>.</summary>
public sealed class WorkerKitBuilder
{
    private readonly List<JobBuilder> _jobBuilders = new();

    internal WorkerKitBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The underlying service collection (for store replacement and extensions).</summary>
    public IServiceCollection Services { get; }

    /// <summary>Host-wide options.</summary>
    public WorkerKitOptions Options { get; } = new();

    /// <summary>Registers a job using the job type's name as its id.</summary>
    public WorkerKitBuilder AddJob<TJob>(Action<JobBuilder<TJob>>? configure = null)
        where TJob : class, IWorkerJob
        => AddJob(typeof(TJob).Name, configure);

    /// <summary>Registers a job with an explicit, stable job id.</summary>
    public WorkerKitBuilder AddJob<TJob>(string jobId, Action<JobBuilder<TJob>>? configure = null)
        where TJob : class, IWorkerJob
    {
        var builder = new JobBuilder<TJob>(jobId);
        configure?.Invoke(builder);
        _jobBuilders.Add(builder);
        Services.AddScoped<TJob>();
        return this;
    }

    internal IReadOnlyList<JobDefinition> BuildDefinitions()
        => _jobBuilders.Select(b => b.Build()).ToList();
}
