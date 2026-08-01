using ReservationReconciliation.Worker.Api;
using ReservationReconciliation.Worker.Domain;
using ResilientWorkerKit;

namespace ReservationReconciliation.Worker.Jobs;

/// <summary>Durable checkpoint of the sync job: where to continue after a crash or restart.</summary>
public sealed record ReservationSyncCheckpoint(string? ContinuationToken, int PagesProcessed);

/// <summary>
/// Pulls reservations page by page from the (fake) API and reconciles them into the ledger.
/// The pattern demonstrated here is the core of ResilientWorkerKit:
/// <list type="number">
/// <item>read the checkpoint → resume from the last fully processed page,</item>
/// <item>per item: acquire an idempotency key <c>reservation:{id}:v{version}</c> — an already
/// completed key is skipped, so a re-delivered item causes no second side effect,</item>
/// <item>invalid items are dead-lettered and marked handled instead of poisoning the batch,</item>
/// <item>the checkpoint advances only after the whole page succeeded.</item>
/// </list>
/// Transient API failures (500, 429) are retried by the HTTP pipeline; if they exhaust, the
/// execution fails, the host and the other jobs keep running, and the next occurrence resumes
/// from the checkpoint.
/// </summary>
public sealed class ReservationSyncJob : IWorkerJob
{
    private readonly IReservationApiClient _apiClient;
    private readonly ReservationLedger _ledger;

    /// <summary>Creates the job (resolved from a per-execution DI scope).</summary>
    public ReservationSyncJob(IReservationApiClient apiClient, ReservationLedger ledger)
    {
        _apiClient = apiClient;
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var checkpoint = await context.Checkpoints.GetAsync<ReservationSyncCheckpoint>(cancellationToken)
            ?? new ReservationSyncCheckpoint(null, 0);

        var continuationToken = checkpoint.ContinuationToken;
        var pagesProcessed = checkpoint.PagesProcessed;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _apiClient.GetReservationsAsync(continuationToken, cancellationToken);
            context.ReportProgress($"page {pagesProcessed + 1} ({page.Items.Count} items)");

            foreach (var reservation in page.Items)
            {
                var idempotencyKey = $"reservation:{reservation.Id}:v{reservation.Version}";
                var acquire = await context.Idempotency.TryAcquireAsync(idempotencyKey, cancellationToken);
                if (acquire != IdempotencyAcquireResult.Acquired)
                {
                    context.Logger.LogInformation(
                        "Reservation {ReservationId} v{Version} already processed; skipping ({AcquireResult})",
                        reservation.Id, reservation.Version, acquire);
                    continue;
                }

                if (reservation.Nights < 0)
                {
                    // Permanently invalid payload: quarantine the item, mark the key handled,
                    // keep processing the rest of the page.
                    await context.DeadLetters.AddAsync(
                        $"reservation:{reservation.Id}",
                        $"Invalid payload: nights={reservation.Nights}",
                        payloadSummary: $"version={reservation.Version}, status={reservation.Status}",
                        cancellationToken);
                    await context.Idempotency.MarkCompletedAsync(idempotencyKey, cancellationToken);
                    continue;
                }

                _ledger.Reconcile(reservation);
                await context.Idempotency.MarkCompletedAsync(idempotencyKey, cancellationToken);
                context.Logger.LogInformation(
                    "Reconciled reservation {ReservationId} v{Version} ({Status})",
                    reservation.Id, reservation.Version, reservation.Status);
            }

            pagesProcessed++;

            // The page is fully processed — only now may the checkpoint advance.
            if (page.NextContinuationToken is null)
            {
                await context.Checkpoints.SaveAsync(
                    new ReservationSyncCheckpoint(null, 0), cancellationToken);
                context.Logger.LogInformation(
                    "Sync pass complete: {Pages} page(s), ledger side effects so far: {SideEffects}",
                    pagesProcessed, _ledger.SideEffectCount);
                return;
            }

            await context.Checkpoints.SaveAsync(
                new ReservationSyncCheckpoint(page.NextContinuationToken, pagesProcessed), cancellationToken);
            continuationToken = page.NextContinuationToken;
        }
    }
}
