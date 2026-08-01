namespace ResilientWorkerKit;

/// <summary>
/// The durable checkpoint of a job: an opaque JSON payload owned by the job code.
/// Must never contain secrets or personal data (see docs/security.md).
/// </summary>
/// <param name="JobId">The owning job.</param>
/// <param name="PayloadJson">JSON-serialized checkpoint state.</param>
/// <param name="PayloadType">Assembly-qualified-less type name of the serialized state (diagnostics only).</param>
/// <param name="UpdatedAtUtc">When the checkpoint was last advanced.</param>
public sealed record JobCheckpoint(
    string JobId,
    string PayloadJson,
    string? PayloadType,
    DateTimeOffset UpdatedAtUtc);
