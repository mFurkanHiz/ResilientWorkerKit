using System.Net;
using ResilientWorkerKit.Engine;

namespace ResilientWorkerKit.UnitTests.Engine;

public class DefaultJobFailureClassifierTests
{
    private readonly DefaultJobFailureClassifier _classifier = new();

    [Fact]
    public void TransientJobException_CarriesRetryAfterHint()
    {
        var classification = _classifier.Classify(
            new TransientJobException("throttled", retryAfter: TimeSpan.FromSeconds(7)));

        Assert.Equal(JobFailureKind.Transient, classification.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7), classification.RetryAfter);
    }

    [Fact]
    public void PermanentJobException_IsPermanent()
        => Assert.Equal(JobFailureKind.Permanent, _classifier.Classify(new PermanentJobException("bad payload")).Kind);

    [Fact]
    public void JobConfigurationException_IsMisconfigured()
        => Assert.Equal(JobFailureKind.Misconfigured, _classifier.Classify(new JobConfigurationException("bad tz")).Kind);

    [Fact]
    public void OperationCanceled_IsCancelled()
        => Assert.Equal(JobFailureKind.Cancelled, _classifier.Classify(new OperationCanceledException()).Kind);

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void RetriableHttpStatuses_AreTransient(HttpStatusCode status)
        => Assert.Equal(JobFailureKind.Transient,
            _classifier.Classify(new HttpRequestException("x", null, status)).Kind);

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void OtherClientErrors_ArePermanent(HttpStatusCode status)
        => Assert.Equal(JobFailureKind.Permanent,
            _classifier.Classify(new HttpRequestException("x", null, status)).Kind);

    [Fact]
    public void NetworkErrorWithoutStatus_IsTransient()
        => Assert.Equal(JobFailureKind.Transient, _classifier.Classify(new HttpRequestException("dns down")).Kind);

    [Fact]
    public void TimeoutException_IsTransient()
        => Assert.Equal(JobFailureKind.Transient, _classifier.Classify(new TimeoutException()).Kind);

    [Fact]
    public void UnknownExceptions_DefaultToTransient()
        => Assert.Equal(JobFailureKind.Transient, _classifier.Classify(new InvalidOperationException("?")).Kind);
}
