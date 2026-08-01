using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
    /// <para>
    /// Safe to call more than once — for example, once by a library module and once by the
    /// application. Later calls add their jobs to the same registry and see the same
    /// <see cref="WorkerKitOptions"/> instance, rather than silently replacing or losing
    /// the earlier registration.
    /// </para>
    /// </summary>
    public static IServiceCollection AddResilientWorkerKit(
        this IServiceCollection services,
        Action<WorkerKitBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var state = GetOrCreateState(services);
        var builder = new WorkerKitBuilder(services, state.Options, state.JobBuilders);
        configure(builder);

        // Hosted services start in registration order, so the engine must always be last:
        // anything the callback registered to prepare durable state (the EF Core schema
        // initializer, a migration runner) has to finish before the first job runs.
        MoveEngineToEndOfHostedServices(services, state);

        return services;
    }

    private static void MoveEngineToEndOfHostedServices(IServiceCollection services, WorkerKitRegistrationState state)
    {
        if (state.EngineHostedService is { } existing)
        {
            services.Remove(existing);
        }

        var descriptor = ServiceDescriptor.Singleton<IHostedService>(
            sp => sp.GetRequiredService<WorkerKitHostedService>());
        state.EngineHostedService = descriptor;
        services.Add(descriptor);
    }

    private static WorkerKitRegistrationState GetOrCreateState(IServiceCollection services)
    {
        var existing = (WorkerKitRegistrationState?)services
            .FirstOrDefault(d => d.ServiceType == typeof(WorkerKitRegistrationState))
            ?.ImplementationInstance;
        if (existing is not null)
        {
            return existing;
        }

        var state = new WorkerKitRegistrationState();
        services.AddSingleton(state);
        services.AddSingleton(state.Options);
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

        // Built on first resolution, i.e. after every AddResilientWorkerKit call has contributed
        // its jobs. This is also where configuration validation runs and fails fast.
        services.TryAddSingleton<IJobRegistry>(_ => new JobRegistry(state.BuildDefinitions()));

        services.TryAddSingleton<WorkerKitHostedService>();
        services.TryAddSingleton<IManualJobTrigger, ManualJobTrigger>();

        // The engine's hosted service is added by MoveEngineToEndOfHostedServices, after the
        // caller's configure callback has had its chance to register initializers.
        return state;
    }
}
