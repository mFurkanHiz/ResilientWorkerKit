using System.Collections.Concurrent;
using System.Net.Http.Json;
using ResilientWorkerKit.Http;

namespace ResilientWorkerKit.IntegrationTests.Infrastructure;

internal sealed record SyncItem(int Id, int Version);

internal sealed record SyncPage(IReadOnlyList<SyncItem> Items, string? NextContinuationToken);

internal sealed record SyncCheckpoint(string? ContinuationToken, int PagesProcessed);

/// <summary>Counts the side effects the sync job applied, so duplicates are observable.</summary>
internal sealed class SideEffectLedger
{
    private readonly ConcurrentBag<string> _applied = new();

    public IReadOnlyCollection<string> Applied => _applied;

    public int Count => _applied.Count;

    public void Apply(SyncItem item) => _applied.Add($"{item.Id}:v{item.Version}");
}

/// <summary>
/// Paged sync job used by the end-to-end scenario: resumes from its checkpoint, guards every
/// item with an idempotency key, and only advances the checkpoint after a page fully succeeds.
/// </summary>
internal sealed class PagedSyncJob : IWorkerJob
{
    private readonly HttpClient _httpClient;
    private readonly SideEffectLedger _ledger;

    public PagedSyncJob(IHttpClientFactory httpClientFactory, SideEffectLedger ledger)
    {
        _httpClient = httpClientFactory.CreateClient("sync-api");
        _ledger = ledger;
    }

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var checkpoint = await context.Checkpoints.GetAsync<SyncCheckpoint>(cancellationToken)
            ?? new SyncCheckpoint(null, 0);

        var token = checkpoint.ContinuationToken;
        var pages = checkpoint.PagesProcessed;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uri = string.IsNullOrEmpty(token) ? "items" : $"items?continuationToken={Uri.EscapeDataString(token)}";
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            await response.EnsureApiSuccessAsync(cancellationToken);
            var page = await response.Content.ReadFromJsonAsync<SyncPage>(cancellationToken)
                ?? throw new ApiRequestException("empty body", response.StatusCode);

            foreach (var item in page.Items)
            {
                var key = $"item:{item.Id}:v{item.Version}";
                if (await context.Idempotency.TryAcquireAsync(key, cancellationToken) != IdempotencyAcquireResult.Acquired)
                {
                    continue;
                }

                _ledger.Apply(item);
                await context.Idempotency.MarkCompletedAsync(key, cancellationToken);
            }

            pages++;
            context.ReportProgress($"page {pages}");

            if (page.NextContinuationToken is null)
            {
                await context.Checkpoints.SaveAsync(new SyncCheckpoint(null, 0), cancellationToken);
                return;
            }

            await context.Checkpoints.SaveAsync(new SyncCheckpoint(page.NextContinuationToken, pages), cancellationToken);
            token = page.NextContinuationToken;
        }
    }
}

/// <summary>Always succeeds — proves the sync job's failures do not affect other jobs.</summary>
internal sealed class SucceedingJob : IWorkerJob
{
    private readonly SucceedingJobCounter _counter;

    public SucceedingJob(SucceedingJobCounter counter) => _counter = counter;

    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        _counter.Increment();
        return Task.CompletedTask;
    }
}

internal sealed class SucceedingJobCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}
