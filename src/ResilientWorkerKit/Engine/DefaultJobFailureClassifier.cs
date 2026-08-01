using System.Net;

namespace ResilientWorkerKit.Engine;

/// <summary>
/// Default exception classification:
/// <list type="number">
/// <item><see cref="IJobFailureHint"/> exceptions carry their own verdict (this is how
/// <c>ResilientWorkerKit.Http</c> and user code influence retries).</item>
/// <item><see cref="OperationCanceledException"/> → <see cref="JobFailureKind.Cancelled"/>
/// (the runner has already distinguished timeout-caused cancellation before asking).</item>
/// <item><see cref="HttpRequestException"/> → transient for 408/429/5xx or missing status,
/// permanent for other 4xx.</item>
/// <item><see cref="TimeoutException"/> → transient.</item>
/// <item>Everything else → transient. This is the conservative default: an unnecessary retry
/// costs a few attempts, while skipping a necessary one loses work. Deterministic failures
/// should throw <see cref="PermanentJobException"/> (or any <see cref="IJobFailureHint"/>).</item>
/// </list>
/// </summary>
public sealed class DefaultJobFailureClassifier : IJobFailureClassifier
{
    /// <inheritdoc />
    public JobFailureClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is IJobFailureHint hint)
        {
            return new JobFailureClassification(hint.FailureKind, hint.RetryAfter);
        }

        return exception switch
        {
            OperationCanceledException => new JobFailureClassification(JobFailureKind.Cancelled),
            HttpRequestException { StatusCode: { } status } => ClassifyHttpStatus(status),
            HttpRequestException => JobFailureClassification.Transient,
            TimeoutException => JobFailureClassification.Transient,
            _ => JobFailureClassification.Transient,
        };
    }

    private static JobFailureClassification ClassifyHttpStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code switch
        {
            408 or 429 or >= 500 => JobFailureClassification.Transient,
            >= 400 and < 500 => JobFailureClassification.Permanent,
            _ => JobFailureClassification.Transient,
        };
    }
}
