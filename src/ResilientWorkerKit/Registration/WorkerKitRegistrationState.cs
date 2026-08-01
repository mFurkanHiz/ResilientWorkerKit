using Microsoft.Extensions.DependencyInjection;

namespace ResilientWorkerKit.Registration;

/// <summary>
/// Accumulates registrations across every <c>AddResilientWorkerKit</c> call on one service
/// collection, so a second call adds jobs instead of being silently dropped by the
/// <c>TryAdd</c> registrations.
/// </summary>
internal sealed class WorkerKitRegistrationState
{
    /// <summary>The single options instance every call configures.</summary>
    public WorkerKitOptions Options { get; } = new();

    /// <summary>Job builders contributed so far, in registration order.</summary>
    public List<JobBuilder> JobBuilders { get; } = new();

    /// <summary>
    /// The engine's <c>IHostedService</c> descriptor. Kept so it can be moved to the end of the
    /// collection after every registration call: hosted services start in registration order,
    /// and anything a caller registers to prepare state — a schema initializer, a migration
    /// runner — must start before the engine runs its first job.
    /// </summary>
    public ServiceDescriptor? EngineHostedService { get; set; }

    /// <summary>Validates and materializes every registered job.</summary>
    public IReadOnlyList<JobDefinition> BuildDefinitions()
        => JobBuilders.Select(b => b.Build()).ToList();
}
