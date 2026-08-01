using System.Text.Json;

namespace ResilientWorkerKit;

/// <summary>Host-wide options for ResilientWorkerKit.</summary>
public sealed class WorkerKitOptions
{
    /// <summary>
    /// Identity of this host instance, recorded on executions and logs.
    /// Defaults to <c>{machine-name}:{process-id}</c>.
    /// </summary>
    public string HostInstanceId { get; set; } =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>
    /// How long a graceful shutdown waits for running executions to observe cancellation and
    /// finish. Must not exceed the host's own shutdown timeout to be effective. Default 30 seconds.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Marks stale <see cref="JobExecutionStatus.Running"/> records as
    /// <see cref="JobExecutionStatus.Abandoned"/> during startup. Default true.
    /// Disable only when multiple host instances share one execution store
    /// (multi-instance coordination is a Phase 2 feature; see docs/limitations.md).
    /// </summary>
    public bool RunStartupRecovery { get; set; } = true;

    /// <summary>
    /// Serializer options used for checkpoint payloads. Defaults to plain
    /// <see cref="JsonSerializerOptions"/> defaults.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new();

    /// <summary>
    /// How long an occurrence waits for the per-job lock before applying the overlap policy.
    /// Default zero (decide immediately).
    /// </summary>
    public TimeSpan LockAcquireTimeout { get; set; } = TimeSpan.Zero;
}
