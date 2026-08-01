using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit.Engine;

/// <summary>Checkpoint accessor bound to one job; serializes payloads as JSON.</summary>
internal sealed class JobCheckpointAccessor : IJobCheckpointAccessor
{
    private readonly IJobCheckpointStore _store;
    private readonly string _jobId;
    private readonly JsonSerializerOptions _json;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<string>? _onSaved;

    public JobCheckpointAccessor(
        IJobCheckpointStore store,
        string jobId,
        JsonSerializerOptions json,
        TimeProvider time,
        ILogger logger,
        Action<string>? onSaved)
    {
        _store = store;
        _jobId = jobId;
        _json = json;
        _time = time;
        _logger = logger;
        _onSaved = onSaved;
    }

    public async Task<T?> GetAsync<T>(CancellationToken cancellationToken = default)
    {
        var checkpoint = await _store.GetAsync(_jobId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return default;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(checkpoint.PayloadJson, _json);
            JobLog.CheckpointLoaded(_logger, checkpoint.PayloadType, checkpoint.UpdatedAtUtc);
            return value;
        }
        catch (JsonException ex)
        {
            throw new JobConfigurationException(
                $"The stored checkpoint of job '{_jobId}' could not be deserialized as {typeof(T).Name}. " +
                "Clear the checkpoint or fix the checkpoint type.", ex);
        }
    }

    public async Task SaveAsync<T>(T checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var json = JsonSerializer.Serialize(checkpoint, _json);
        var record = new JobCheckpoint(_jobId, json, typeof(T).FullName, _time.GetUtcNow());
        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);

        var summary = Summarize(typeof(T).Name, json);
        JobLog.CheckpointSaved(_logger, summary);
        _onSaved?.Invoke(summary);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
        => _store.DeleteAsync(_jobId, cancellationToken);

    private static string Summarize(string typeName, string json)
    {
        const int maxJson = 160;
        var body = json.Length <= maxJson ? json : json[..maxJson] + "…";
        return $"{typeName} {body}";
    }
}

/// <summary>Idempotency accessor bound to one job and execution.</summary>
internal sealed class JobIdempotencyAccessor : IJobIdempotencyAccessor
{
    private readonly IIdempotencyStore _store;
    private readonly string _jobId;
    private readonly string _executionId;
    private readonly TimeSpan? _timeToLive;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public JobIdempotencyAccessor(
        IIdempotencyStore store,
        string jobId,
        string executionId,
        TimeSpan? timeToLive,
        TimeProvider time,
        ILogger logger)
    {
        _store = store;
        _jobId = jobId;
        _executionId = executionId;
        _timeToLive = timeToLive;
        _time = time;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var exists = await _store.ExistsCompletedAsync(_jobId, key, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            JobLog.IdempotentItemSkipped(_logger, key);
        }

        return exists;
    }

    public async Task<IdempotencyAcquireResult> TryAcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var expires = _timeToLive is { } ttl ? _time.GetUtcNow() + ttl : (DateTimeOffset?)null;
        var result = await _store.TryAcquireAsync(_jobId, key, _executionId, expires, cancellationToken).ConfigureAwait(false);
        if (result == IdempotencyAcquireResult.AlreadyCompleted)
        {
            JobLog.IdempotentItemSkipped(_logger, key);
        }

        return result;
    }

    public Task MarkCompletedAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _store.MarkCompletedAsync(_jobId, key, cancellationToken);
    }

    public Task MarkFailedAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _store.MarkFailedAsync(_jobId, key, cancellationToken);
    }
}

/// <summary>Dead-letter accessor bound to one job and execution.</summary>
internal sealed class JobDeadLetterAccessor : IJobDeadLetterAccessor
{
    private readonly IDeadLetterStore _store;
    private readonly string _jobId;
    private readonly string _executionId;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly WorkerKitMetrics _metrics;

    public JobDeadLetterAccessor(
        IDeadLetterStore store,
        string jobId,
        string executionId,
        TimeProvider time,
        ILogger logger,
        WorkerKitMetrics metrics)
    {
        _store = store;
        _jobId = jobId;
        _executionId = executionId;
        _time = time;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task AddAsync(string itemId, string reason, string? payloadSummary = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var record = new DeadLetterRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            JobId = _jobId,
            ExecutionId = _executionId,
            Scope = "item",
            ItemId = itemId,
            Reason = reason,
            PayloadSummary = payloadSummary,
            AttemptCount = 1,
            CreatedAtUtc = _time.GetUtcNow(),
        };

        await _store.AddAsync(record, cancellationToken).ConfigureAwait(false);
        JobLog.DeadLetterCreated(_logger, "item", itemId, reason);
        _metrics.DeadLetterCreated(_jobId);
    }
}
