using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResilientWorkerKit.Engine;
using ResilientWorkerKit.Registration;
using ResilientWorkerKit.Stores;

namespace ResilientWorkerKit;

/// <summary>Dependency-injection entry point for ResilientWorkerKit.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the ResilientWorkerKit engine and the configured jobs to the service collection.
    /// In-memory stores are registered by default (tests/demos only); replace them with the
    /// EF Core stores for durable state (<c>ResilientWorkerKit.EntityFrameworkCore</c>).
    /// Job configuration is validated when the host starts; invalid configuration fails fast
    /// with <see cref="JobConfigurationException"/>.
    /// </summary>
    public static IServiceCollection AddResilientWorkerKit(
        this IServiceCollection services,
        Action<WorkerKitBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkerKitBuilder(services);
        configure(builder);

        services.AddSingleton(builder.Options);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IJobCheckpointStore, InMemoryJobCheckpointStore>();
        services.TryAddSingleton<IJobExecutionStore, InMemoryJobExecutionStore>();
        services.TryAddSingleton<IIdempotencyStore>(sp =>
            new InMemoryIdempotencyStore(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IDeadLetterStore, InMemoryDeadLetterStore>();
        services.TryAddSingleton<IJobLockProvider, InProcessJobLockProvider>();
        services.TryAddSingleton<IJobFailureClassifier, DefaultJobFailureClassifier>();

        services.TryAddSingleton<WorkerKitMetrics>();
        services.TryAddSingleton<JobHealthTracker>();
        services.TryAddSingleton<IJobHealthTracker>(sp => sp.GetRequiredService<JobHealthTracker>());
        services.TryAddSingleton<IJobProgressReporter>(sp => sp.GetRequiredService<JobHealthTracker>());

        services.TryAddSingleton<JobRunner>();
        services.TryAddSingleton<IJobRegistry>(_ => new JobRegistry(builder.BuildDefinitions()));
        services.TryAddSingleton<WorkerKitHostedService>();
        services.TryAddSingleton<IManualJobTrigger, ManualJobTrigger>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkerKitHostedService>());

        return services;
    }
}
