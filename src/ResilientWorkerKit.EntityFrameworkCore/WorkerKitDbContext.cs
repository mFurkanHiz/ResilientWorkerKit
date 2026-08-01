using Microsoft.EntityFrameworkCore;

namespace ResilientWorkerKit.EntityFrameworkCore;

/// <summary>
/// The ResilientWorkerKit persistence model. Provider-agnostic: tested with SQLite, designed
/// to be compatible with SQL Server and other relational providers. The host application owns
/// migrations (see docs/persistence.md); for demos and tests
/// <see cref="WorkerKitEfCoreOptions.AutoCreateSchema"/> can create the schema directly.
/// </summary>
public sealed class WorkerKitDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    public WorkerKitDbContext(DbContextOptions<WorkerKitDbContext> options) : base(options)
    {
    }

    /// <summary>Execution history.</summary>
    public DbSet<JobExecutionEntity> Executions => Set<JobExecutionEntity>();

    /// <summary>Job checkpoints.</summary>
    public DbSet<JobCheckpointEntity> Checkpoints => Set<JobCheckpointEntity>();

    /// <summary>Idempotency records.</summary>
    public DbSet<JobIdempotencyEntity> IdempotencyRecords => Set<JobIdempotencyEntity>();

    /// <summary>Dead letters.</summary>
    public DbSet<JobDeadLetterEntity> DeadLetters => Set<JobDeadLetterEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyWorkerKitModel();
}

/// <summary>
/// Model configuration, exposed separately so applications that prefer a single DbContext can
/// embed the ResilientWorkerKit tables into their own model.
/// </summary>
public static class WorkerKitModelBuilderExtensions
{
    /// <summary>Applies the ResilientWorkerKit entity configuration to the model.</summary>
    public static ModelBuilder ApplyWorkerKitModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<JobExecutionEntity>(entity =>
        {
            entity.ToTable("WorkerKitExecutions");
            entity.HasKey(e => e.ExecutionId);
            entity.Property(e => e.ExecutionId).HasMaxLength(64);
            entity.Property(e => e.JobId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ScheduledExecutionId).HasMaxLength(300).IsRequired();
            entity.Property(e => e.TriggerType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.TimeZoneId).HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.FailureKind).HasConversion<string?>().HasMaxLength(20);
            entity.Property(e => e.ErrorType).HasMaxLength(300);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);
            entity.Property(e => e.ErrorDetail).HasMaxLength(4000);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.HostInstanceId).HasMaxLength(200);
            entity.Property(e => e.LastCheckpointSummary).HasMaxLength(500);
            entity.HasIndex(e => new { e.JobId, e.StartedAtUtc });
            entity.HasIndex(e => new { e.JobId, e.ScheduledExecutionId });
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<JobCheckpointEntity>(entity =>
        {
            entity.ToTable("WorkerKitCheckpoints");
            entity.HasKey(e => e.JobId);
            entity.Property(e => e.JobId).HasMaxLength(200);
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.PayloadType).HasMaxLength(300);
        });

        modelBuilder.Entity<JobIdempotencyEntity>(entity =>
        {
            entity.ToTable("WorkerKitIdempotencyRecords");
            // The composite primary key IS the idempotency guarantee: of two concurrent
            // inserts for the same (JobId, Key), exactly one succeeds at the database.
            entity.HasKey(e => new { e.JobId, e.Key });
            entity.Property(e => e.JobId).HasMaxLength(200);
            entity.Property(e => e.Key).HasMaxLength(400);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ExecutionId).HasMaxLength(64);
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasIndex(e => e.ExpiresAtUtc);
        });

        modelBuilder.Entity<JobDeadLetterEntity>(entity =>
        {
            entity.ToTable("WorkerKitDeadLetters");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.JobId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ExecutionId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Scope).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ItemId).HasMaxLength(300);
            entity.Property(e => e.FailureKind).HasConversion<string?>().HasMaxLength(20);
            entity.Property(e => e.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.PayloadSummary).HasMaxLength(2000);
            entity.HasIndex(e => new { e.JobId, e.CreatedAtUtc });
        });

        return modelBuilder;
    }
}
