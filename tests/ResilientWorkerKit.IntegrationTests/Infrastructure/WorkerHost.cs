using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResilientWorkerKit.EntityFrameworkCore;

namespace ResilientWorkerKit.IntegrationTests.Infrastructure;

/// <summary>
/// Builds and runs a real .NET Generic Host with ResilientWorkerKit registered against a real
/// SQLite database, so restart scenarios exercise the same code path production would.
/// </summary>
internal sealed class WorkerHost : IAsyncDisposable
{
    private readonly IHost _host;

    private WorkerHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public static async Task<WorkerHost> StartAsync(
        SqliteDatabase database,
        Action<WorkerKitBuilder> configureKit,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        configureServices?.Invoke(builder.Services);

        builder.Services.AddResilientWorkerKit(kit =>
        {
            kit.Options.ShutdownGracePeriod = TimeSpan.FromSeconds(5);
            kit.UseEntityFrameworkCore(
                db => db.UseSqlite(database.ConnectionString),
                ef => ef.AutoCreateSchema = true);
            configureKit(kit);
        });

        var host = builder.Build();
        await host.StartAsync();
        return new WorkerHost(host);
    }

    public T GetRequiredService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    /// <summary>Polls a condition without blocking the host (bounded, no Thread.Sleep).</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The awaited condition never became true.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }

        _host.Dispose();
    }
}

/// <summary>Direct access to the durable stores of a host (assertions and seeding).</summary>
internal static class StoreAccess
{
    public static IJobExecutionStore Executions(this WorkerHost host) => host.GetRequiredService<IJobExecutionStore>();

    public static IJobCheckpointStore Checkpoints(this WorkerHost host) => host.GetRequiredService<IJobCheckpointStore>();

    public static IIdempotencyStore Idempotency(this WorkerHost host) => host.GetRequiredService<IIdempotencyStore>();

    public static IDeadLetterStore DeadLetters(this WorkerHost host) => host.GetRequiredService<IDeadLetterStore>();

    public static async Task<IReadOnlyList<JobExecutionRecord>> HistoryAsync(this WorkerHost host, string jobId)
        => await host.Executions().GetRecentAsync(jobId, 200);

    public static async Task<int> CountAsync(this WorkerHost host, string jobId, JobExecutionStatus? status = null)
        => (await host.HistoryAsync(jobId)).Count(r => status is null || r.Status == status);
}
