using ReservationReconciliation.Worker.Api;
using ResilientWorkerKit;

namespace ReservationReconciliation.Worker.Jobs;

/// <summary>
/// Dispatches one (simulated) notification per run. Its real purpose in the sample: keep
/// succeeding while <see cref="ReservationSyncJob"/> fails, proving per-job failure isolation.
/// </summary>
public sealed class NotificationDispatchJob : IWorkerJob
{
    private readonly IReservationApiClient _apiClient;

    /// <summary>Creates the job.</summary>
    public NotificationDispatchJob(IReservationApiClient apiClient) => _apiClient = apiClient;

    /// <inheritdoc />
    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        // A stable business identity as the idempotency key: one delivery per execution here,
        // but retried HTTP attempts of the same delivery reuse the same key.
        var idempotencyKey = $"notification:{context.ExecutionId}";
        await _apiClient.SendNotificationAsync(idempotencyKey, cancellationToken);
        context.Logger.LogInformation("Notification dispatched");
    }
}
