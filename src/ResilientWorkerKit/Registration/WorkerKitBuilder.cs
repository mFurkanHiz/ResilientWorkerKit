using Microsoft.Extensions.DependencyInjection;

namespace ResilientWorkerKit;

/// <summary>Root configuration surface of <c>AddResilientWorkerKit</c>.</summary>
public sealed class WorkerKitBuilder
{
    private readonly List<JobBuilder> _jobBuilders;

    internal WorkerKitBuilder(IServiceCollection services, WorkerKitOptions options, List<JobBuilder> jobBuilders)
    {
        Services = services;
        Options = options;
        _jobBuilders = jobBuilders;
    }

    /// <summary>The underlying service collection (for store replacement and extensions).</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Host-wide options. The same instance is shared by every <c>AddResilientWorkerKit</c> call
    /// on this service collection.
    /// </summary>
    public WorkerKitOptions Options { get; }

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
        Services.TryAddJobType<TJob>();
        return this;
    }
}

internal static class JobTypeRegistration
{
    /// <summary>
    /// Registers the job type as scoped exactly once, so registering two jobs backed by the same
    /// implementation type does not add duplicate descriptors.
    /// </summary>
    public static void TryAddJobType<TJob>(this IServiceCollection services)
        where TJob : class, IWorkerJob
    {
        if (!services.Any(d => d.ServiceType == typeof(TJob)))
        {
            services.AddScoped<TJob>();
        }
    }
}
