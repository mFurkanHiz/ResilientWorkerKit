using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit.EntityFrameworkCore;

/// <summary>Options for the EF Core persistence integration.</summary>
public sealed class WorkerKitEfCoreOptions
{
    /// <summary>
    /// Creates the schema with <c>EnsureCreated</c> at startup. Convenient for demos, samples
    /// and tests; production deployments should own the schema via EF Core migrations instead
    /// (see docs/persistence.md). Default false.
    /// </summary>
    public bool AutoCreateSchema { get; set; }
}

/// <summary>EF Core registration for ResilientWorkerKit durable stores.</summary>
public static class WorkerKitEfCoreExtensions
{
    /// <summary>
    /// Replaces the in-memory stores with EF Core stores backed by <see cref="WorkerKitDbContext"/>.
    /// Works with SQLite, SQL Server and other relational providers:
    /// <code>
    /// kit.UseEntityFrameworkCore(db => db.UseSqlite(connectionString));
    /// </code>
    /// </summary>
    public static WorkerKitBuilder UseEntityFrameworkCore(
        this WorkerKitBuilder builder,
        Action<DbContextOptionsBuilder> configureDbContext,
        Action<WorkerKitEfCoreOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        var efOptions = new WorkerKitEfCoreOptions();
        configureOptions?.Invoke(efOptions);

        builder.Services.AddDbContextFactory<WorkerKitDbContext>(configureDbContext);
        builder.Services.AddSingleton<IJobCheckpointStore, EfCoreJobCheckpointStore>();
        builder.Services.AddSingleton<IJobExecutionStore, EfCoreJobExecutionStore>();
        builder.Services.AddSingleton<IIdempotencyStore, EfCoreIdempotencyStore>();
        builder.Services.AddSingleton<IDeadLetterStore, EfCoreDeadLetterStore>();
        builder.Services.AddSingleton<IPendingOccurrenceStore, EfCorePendingOccurrenceStore>();

        if (efOptions.AutoCreateSchema)
        {
            // Registered inside AddResilientWorkerKit's configure callback, i.e. before the
            // engine's own hosted service — so the schema exists before any job runs.
            builder.Services.AddHostedService<WorkerKitSchemaInitializer>();
        }

        return builder;
    }
}

/// <summary>Creates the ResilientWorkerKit schema at startup (demo/test convenience).</summary>
internal sealed class WorkerKitSchemaInitializer : IHostedService
{
    private readonly IDbContextFactory<WorkerKitDbContext> _factory;
    private readonly ILogger<WorkerKitSchemaInitializer> _logger;

    public WorkerKitSchemaInitializer(
        IDbContextFactory<WorkerKitDbContext> factory,
        ILogger<WorkerKitSchemaInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var created = await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            if (created)
            {
                _logger.LogInformation("ResilientWorkerKit schema created (EnsureCreated). Use EF Core migrations for production deployments.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
