using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ResilientWorkerKit.HealthChecks;

/// <summary>Health check registration.</summary>
public static class HealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds the ResilientWorkerKit job health check:
    /// <code>services.AddHealthChecks().AddResilientWorkerKit();</code>
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">Health check name. Default <c>resilient-worker-kit</c>.</param>
    /// <param name="failureStatus">Status reported when the check itself fails. Default Unhealthy.</param>
    /// <param name="tags">Optional tags (e.g. "ready").</param>
    public static IHealthChecksBuilder AddResilientWorkerKit(
        this IHealthChecksBuilder builder,
        string name = "resilient-worker-kit",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new WorkerKitHealthCheck(
                sp.GetRequiredService<IJobHealthTracker>(),
                sp.GetRequiredService<IJobRegistry>(),
                sp.GetRequiredService<TimeProvider>()),
            failureStatus,
            tags));
    }
}
