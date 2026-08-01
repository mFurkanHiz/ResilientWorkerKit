namespace ResilientWorkerKit;

/// <summary>Item-level dead-letter recording for the current execution.</summary>
public interface IJobDeadLetterAccessor
{
    /// <summary>
    /// Records an item that could not be processed. <paramref name="payloadSummary"/> must be
    /// masked/summarized — never a raw payload containing secrets or personal data.
    /// </summary>
    Task AddAsync(string itemId, string reason, string? payloadSummary = null, CancellationToken cancellationToken = default);
}
