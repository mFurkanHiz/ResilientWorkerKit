namespace ResilientWorkerKit;

/// <summary>
/// Typed checkpoint access for the current job. Save a checkpoint only after the
/// corresponding batch of work has fully and durably succeeded (see docs/checkpoints.md).
/// </summary>
public interface IJobCheckpointAccessor
{
    /// <summary>Returns the current checkpoint deserialized as <typeparamref name="T"/>, or default when none exists.</summary>
    /// <exception cref="JobConfigurationException">The stored payload cannot be deserialized as <typeparamref name="T"/>.</exception>
    Task<T?> GetAsync<T>(CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the job's checkpoint.</summary>
    Task SaveAsync<T>(T checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Deletes the job's checkpoint (the next execution starts from scratch).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
