using Microsoft.EntityFrameworkCore;
using ResilientWorkerKit.EntityFrameworkCore;
using ResilientWorkerKit.IntegrationTests.Infrastructure;
using ResilientWorkerKit.Stores;

namespace ResilientWorkerKit.IntegrationTests;

/// <summary>
/// The lease contract of <see cref="IPendingOccurrenceStore"/>, asserted identically against
/// every implementation: single winner, expiry takeover, owner-token checks, visibility-based
/// recovery, and uniqueness per logical occurrence. What passes here is exactly what makes the
/// pending-occurrence capability safe when two hosts share one database — no more, no less;
/// the engine as a whole remains single-instance (see docs/limitations.md).
/// </summary>
public abstract class PendingOccurrenceLeaseContract
{
    private const string JobId = "lease-job";
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-15T07:00:00Z");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private protected abstract Task<IPendingOccurrenceStore> CreateStoreAsync();

    private static PendingOccurrence Row(
        string id = "row-1",
        string identity = "at:2026-08-15T07:00:00Z+followup-1",
        DateTimeOffset? dueAtUtc = null,
        string jobId = JobId)
        => new()
        {
            Id = id,
            JobId = jobId,
            DueAtUtc = dueAtUtc ?? T0,
            IdentityToken = identity,
            OriginScheduledExecutionId = $"{jobId}:at:2026-08-15T07:00:00Z",
            FollowUpOrdinal = 1,
            CreatedAtUtc = T0.AddMinutes(-5),
        };

    [SkippableFact]
    public async Task Add_ThenGetNext_RoundTrips()
    {
        var store = await CreateStoreAsync();

        Assert.True(await store.AddAsync(Row()));

        var next = await store.GetNextAsync(JobId, T0);
        Assert.NotNull(next);
        Assert.Equal("row-1", next.Id);
        Assert.Null(next.LeaseOwner);
        Assert.Null(next.LeaseExpiresAtUtc);
    }

    [SkippableFact]
    public async Task Add_SameLogicalOccurrenceTwice_ReportsAlreadyQueued()
    {
        var store = await CreateStoreAsync();

        Assert.True(await store.AddAsync(Row(id: "row-1")));
        Assert.False(await store.AddAsync(Row(id: "row-2"))); // same (JobId, IdentityToken)

        Assert.Equal(1, await store.CountAsync(JobId));
    }

    [SkippableFact]
    public async Task Add_SameIdentityForAnotherJob_IsIndependent()
    {
        var store = await CreateStoreAsync();

        Assert.True(await store.AddAsync(Row(id: "row-1")));
        Assert.True(await store.AddAsync(Row(id: "row-2", jobId: "other-job")));
    }

    [SkippableFact]
    public async Task Acquire_ThenComplete_RemovesTheRow()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());

        var token = await store.TryAcquireLeaseAsync("row-1", "host-a", Lease, T0);
        Assert.NotNull(token);

        Assert.True(await store.CompleteAsync("row-1", token));
        Assert.Equal(0, await store.CountAsync(JobId));
        Assert.Null(await store.GetNextAsync(JobId, T0));
    }

    [SkippableFact]
    public async Task ConcurrentAcquire_ExactlyOneWinner()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());

        var tokens = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => store.TryAcquireLeaseAsync("row-1", $"host-{i}", Lease, T0))));

        Assert.Single(tokens, t => t is not null);
    }

    [SkippableFact]
    public async Task Acquire_WhileLeaseHeld_Fails()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());

        Assert.NotNull(await store.TryAcquireLeaseAsync("row-1", "host-a", Lease, T0));
        Assert.Null(await store.TryAcquireLeaseAsync("row-1", "host-b", Lease, T0.AddMinutes(4)));
    }

    [SkippableFact]
    public async Task ExpiredLease_CanBeTakenOver_AndTheOldTokenIsDead()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());

        var deadHostToken = await store.TryAcquireLeaseAsync("row-1", "dead-host", Lease, T0);
        Assert.NotNull(deadHostToken);

        // "dead-host" never completes; past the expiry a new owner takes over.
        var takeoverAt = T0 + Lease + TimeSpan.FromSeconds(1);
        var newToken = await store.TryAcquireLeaseAsync("row-1", "host-b", Lease, takeoverAt);
        Assert.NotNull(newToken);

        // The dead host's token can no longer do anything to the row.
        Assert.False(await store.CompleteAsync("row-1", deadHostToken));
        Assert.False(await store.ReleaseAsync("row-1", deadHostToken));
        Assert.False(await store.TryRenewLeaseAsync("row-1", deadHostToken, Lease, takeoverAt));

        Assert.True(await store.CompleteAsync("row-1", newToken));
    }

    [SkippableFact]
    public async Task ALease_IsAcquirable_AtExactlyItsExpiryInstant()
    {
        // GetNextAsync surfaces a leased row AT its expiry, so a scheduler woken exactly then
        // must be able to acquire — an exclusive boundary would leave it spinning one instant
        // short of the predicate.
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());

        Assert.NotNull(await store.TryAcquireLeaseAsync("row-1", "host-a", Lease, T0));
        Assert.NotNull(await store.TryAcquireLeaseAsync("row-1", "host-b", Lease, T0 + Lease));
    }

    [SkippableFact]
    public async Task Renew_ExtendsTheLease()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());

        var token = await store.TryAcquireLeaseAsync("row-1", "host-a", Lease, T0);
        Assert.True(await store.TryRenewLeaseAsync("row-1", token!, Lease, T0.AddMinutes(4)));

        // Original expiry (T0+5) has passed, but the renewal moved it to T0+9.
        Assert.Null(await store.TryAcquireLeaseAsync("row-1", "host-b", Lease, T0.AddMinutes(6)));

        // Past the renewed expiry it is acquirable again.
        Assert.NotNull(await store.TryAcquireLeaseAsync("row-1", "host-b", Lease, T0.AddMinutes(10)));
    }

    [SkippableFact]
    public async Task NonOwner_CannotCompleteReleaseOrRenew()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());
        await store.TryAcquireLeaseAsync("row-1", "host-a", Lease, T0);

        Assert.False(await store.CompleteAsync("row-1", "not-the-token"));
        Assert.False(await store.ReleaseAsync("row-1", "not-the-token"));
        Assert.False(await store.TryRenewLeaseAsync("row-1", "not-the-token", Lease, T0));

        Assert.Equal(1, await store.CountAsync(JobId));
    }

    [SkippableFact]
    public async Task Release_MakesTheRowImmediatelyAcquirable()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());

        var token = await store.TryAcquireLeaseAsync("row-1", "host-a", Lease, T0);
        Assert.True(await store.ReleaseAsync("row-1", token!));

        var next = await store.GetNextAsync(JobId, T0.AddSeconds(1));
        Assert.NotNull(next);
        Assert.Null(next.LeaseOwner);
        Assert.Null(next.LeaseExpiresAtUtc);

        Assert.NotNull(await store.TryAcquireLeaseAsync("row-1", "host-b", Lease, T0.AddSeconds(1)));
    }

    [SkippableFact]
    public async Task GetNext_SurfacesALeasedRow_NoEarlierThanItsLeaseExpiry()
    {
        var store = await CreateStoreAsync();

        // A is due first but leased until T0+5; B is due at T0+2 and free. Effective order
        // puts B first — a scheduler sleeping on this answer wakes for B, not for A's owner.
        await store.AddAsync(Row(id: "row-a", identity: "id-a", dueAtUtc: T0));
        await store.AddAsync(Row(id: "row-b", identity: "id-b", dueAtUtc: T0.AddMinutes(2)));
        await store.TryAcquireLeaseAsync("row-a", "host-a", Lease, T0);

        var next = await store.GetNextAsync(JobId, T0.AddMinutes(1));
        Assert.Equal("row-b", next!.Id);

        // With B gone, A is surfaced — carrying the lease information the caller needs in
        // order to wait until the lease could expire rather than spinning.
        var tokenB = await store.TryAcquireLeaseAsync("row-b", "host-b", Lease, T0.AddMinutes(2));
        await store.CompleteAsync("row-b", tokenB!);

        next = await store.GetNextAsync(JobId, T0.AddMinutes(3));
        Assert.Equal("row-a", next!.Id);
        Assert.Equal("host-a", next.LeaseOwner);
        Assert.Equal(T0 + Lease, next.LeaseExpiresAtUtc);
    }

    [SkippableFact]
    public async Task Complete_IsIdempotentlyFinal()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Row());
        var token = await store.TryAcquireLeaseAsync("row-1", "host-a", Lease, T0);

        Assert.True(await store.CompleteAsync("row-1", token!));
        Assert.False(await store.CompleteAsync("row-1", token!));
        Assert.False(await store.ReleaseAsync("row-1", token!));
    }
}

/// <summary>The contract against the in-memory store (the engine's test double).</summary>
public sealed class InMemoryPendingOccurrenceLeaseTests : PendingOccurrenceLeaseContract
{
    private protected override Task<IPendingOccurrenceStore> CreateStoreAsync()
        => Task.FromResult<IPendingOccurrenceStore>(new InMemoryPendingOccurrenceStore());
}

/// <summary>The contract against SQLite — the provider every developer machine runs.</summary>
public sealed class SqlitePendingOccurrenceLeaseTests : PendingOccurrenceLeaseContract, IDisposable
{
    private readonly SqliteDatabase _database = new();

    private protected override async Task<IPendingOccurrenceStore> CreateStoreAsync()
    {
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<WorkerKitDbContext>()
                .UseSqlite(_database.ConnectionString)
                .Options);
        var db = factory.CreateDbContext();
        await using (db)
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new EfCorePendingOccurrenceStore(factory);
    }

    public void Dispose() => _database.Dispose();
}

/// <summary>
/// The contract against SQL Server. Runs whenever <c>RWK_SQLSERVER_CONNECTION</c> points at a
/// reachable server — in CI that is a service container on the Linux leg — and skips loudly
/// everywhere else. This is what backs the "SQL Server compatible" claim with evidence.
/// </summary>
public sealed class SqlServerPendingOccurrenceLeaseTests : PendingOccurrenceLeaseContract, IAsyncLifetime
{
    /// <summary>
    /// Base connection string of the SQL Server to test against, or null to skip. In CI this
    /// points at the Linux leg's service container; locally it is usually unset.
    /// </summary>
    internal static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable("RWK_SQLSERVER_CONNECTION") is { Length: > 0 } value ? value : null;

    private readonly string _databaseName = $"WorkerKitTests_{Guid.NewGuid():n}";
    private string? _connectionString;

    private protected override async Task<IPendingOccurrenceStore> CreateStoreAsync()
    {
        Skip.If(
            _connectionString is null,
            "RWK_SQLSERVER_CONNECTION is not set; the SQL Server leg runs in CI (see .github/workflows/ci.yml).");

        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<WorkerKitDbContext>()
                .UseSqlServer(_connectionString)
                .Options);
        var db = factory.CreateDbContext();
        await using (db)
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new EfCorePendingOccurrenceStore(factory);
    }

    public Task InitializeAsync()
    {
        if (ServerConnectionString is { } server)
        {
            // One database per test class instance keeps the [Fact]s independent.
            _connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server)
            {
                InitialCatalog = _databaseName,
            }.ConnectionString;
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<WorkerKitDbContext>()
                .UseSqlServer(_connectionString)
                .Options);
        var db = factory.CreateDbContext();
        await using (db)
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}

/// <summary>Minimal <see cref="IDbContextFactory{TContext}"/> for store-level tests.</summary>
internal sealed class TestDbContextFactory : IDbContextFactory<WorkerKitDbContext>
{
    private readonly DbContextOptions<WorkerKitDbContext> _options;

    public TestDbContextFactory(DbContextOptions<WorkerKitDbContext> options) => _options = options;

    public WorkerKitDbContext CreateDbContext() => new(_options);
}
