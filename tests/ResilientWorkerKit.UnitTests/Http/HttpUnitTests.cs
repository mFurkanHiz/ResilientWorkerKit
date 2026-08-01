using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using ResilientWorkerKit.Http;

namespace ResilientWorkerKit.UnitTests.Http;

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }
}

public class SensitiveDataMaskerTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("X-Api-Key")]
    public void KnownSensitiveHeaders_AreDetected(string header)
        => Assert.True(SensitiveDataMasker.IsSensitiveHeader(header));

    [Fact]
    public void AdditionalHeaders_AreDetected()
        => Assert.True(SensitiveDataMasker.IsSensitiveHeader("X-Custom-Secret", ["X-Custom-Secret"]));

    [Fact]
    public void BearerTokens_AreMasked()
    {
        var masked = SensitiveDataMasker.MaskSecrets("failed with Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig attached");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", masked);
        Assert.Contains("Bearer ***", masked);
    }

    [Theory]
    [InlineData("api_key=super-secret-value", "api_key=***")]
    [InlineData("client_secret: abc123", "client_secret:***")]
    [InlineData("password=hunter2&other=1", "password=***")]
    [InlineData("access_token=tok123", "access_token=***")]
    public void KeyValueSecrets_AreMasked(string input, string expectedFragment)
        => Assert.Contains(expectedFragment, SensitiveDataMasker.MaskSecrets(input).Replace(" ", ""));

    [Fact]
    public void PlainText_IsUntouched()
        => Assert.Equal("nothing secret here", SensitiveDataMasker.MaskSecrets("nothing secret here"));
}

public class CorrelationAndIdempotencyHandlerTests
{
    private static async Task<HttpRequestMessage> Send(DelegatingHandler handler, HttpRequestMessage request)
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        handler.InnerHandler = stub;
        using var invoker = new HttpMessageInvoker(handler);
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        return stub.Requests.Single();
    }

    [Fact]
    public async Task CorrelationId_IsAdded_WhenMissing()
    {
        var options = new ResilientApiClientOptions();
        var sent = await Send(new CorrelationIdHandler(options),
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"));

        Assert.True(sent.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task ExplicitCorrelationId_IsPreserved()
    {
        var options = new ResilientApiClientOptions();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x")
            .WithCorrelationId("my-correlation");

        var sent = await Send(new CorrelationIdHandler(options), request);

        Assert.Equal("my-correlation", sent.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task IdempotencyKey_AddedToPost_NotToGet()
    {
        var options = new ResilientApiClientOptions { EnableIdempotencyKey = true };

        var post = await Send(new IdempotencyKeyHandler(options),
            new HttpRequestMessage(HttpMethod.Post, "https://api.example.test/x"));
        var get = await Send(new IdempotencyKeyHandler(options),
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"));

        Assert.True(post.Headers.Contains("Idempotency-Key"));
        Assert.False(get.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task ExplicitIdempotencyKey_IsUsed()
    {
        var options = new ResilientApiClientOptions { EnableIdempotencyKey = true };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.test/x")
            .WithIdempotencyKey("order:42:v1");

        var sent = await Send(new IdempotencyKeyHandler(options), request);

        Assert.Equal("order:42:v1", sent.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task ApiKeyHandler_AttachesTheKey()
    {
        var handler = new ApiKeyHandler("X-Api-Key", new FixedApiKeyProvider("k-123"));
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        handler.InnerHandler = stub;
        using var invoker = new HttpMessageInvoker(handler);

        (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), CancellationToken.None)).Dispose();

        Assert.Equal("k-123", stub.Requests.Single().Headers.GetValues("X-Api-Key").Single());
    }

    private sealed class FixedApiKeyProvider(string key) : IApiKeyProvider
    {
        public ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken = default) => new(key);
    }
}

public class BearerTokenTests
{
    private sealed class CountingTokenProvider : IBearerTokenProvider
    {
        public int Calls;
        public TimeSpan? Lifetime { get; init; }
        private readonly TimeProvider _time;

        public CountingTokenProvider(TimeProvider time) => _time = time;

        public ValueTask<BearerToken> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return new(new BearerToken($"token-{Calls}",
                Lifetime is { } l ? _time.GetUtcNow() + l : null));
        }
    }

    [Fact]
    public async Task CachingProvider_ReusesTokenUntilExpiry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T10:00:00Z"));
        var inner = new CountingTokenProvider(time) { Lifetime = TimeSpan.FromMinutes(10) };
        using var caching = new CachingBearerTokenProvider(inner, time);

        var t1 = await caching.GetTokenAsync();
        var t2 = await caching.GetTokenAsync();
        time.Advance(TimeSpan.FromMinutes(11));
        var t3 = await caching.GetTokenAsync();

        Assert.Equal(t1.AccessToken, t2.AccessToken);
        Assert.NotEqual(t1.AccessToken, t3.AccessToken);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Handler_AttachesBearerToken()
    {
        var time = new FakeTimeProvider();
        var handler = new BearerTokenHandler(new CountingTokenProvider(time));
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        handler.InnerHandler = stub;
        using var invoker = new HttpMessageInvoker(handler);

        (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), CancellationToken.None)).Dispose();

        Assert.Equal("Bearer", stub.Requests.Single().Headers.Authorization!.Scheme);
        Assert.Equal("token-1", stub.Requests.Single().Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Unauthorized_InvalidatesTheCachedToken()
    {
        var time = new FakeTimeProvider();
        var inner = new CountingTokenProvider(time) { Lifetime = TimeSpan.FromHours(1) };
        using var caching = new CachingBearerTokenProvider(inner, time);
        var handler = new BearerTokenHandler(caching);
        handler.InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var invoker = new HttpMessageInvoker(handler);

        (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), CancellationToken.None)).Dispose();
        (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"), CancellationToken.None)).Dispose();

        Assert.Equal(2, inner.Calls); // second request re-acquired after the 401 invalidation
    }
}

public class EnsureApiSuccessTests
{
    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                "https://api.example.test/reservations?apiKey=should-not-leak"),
        };
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/problem+json");
        }

        return response;
    }

    [Fact]
    public async Task Success_DoesNotThrow()
    {
        using var response = Response(HttpStatusCode.OK);
        await response.EnsureApiSuccessAsync();
    }

    [Fact]
    public async Task BadRequest_IsPermanent()
    {
        using var response = Response(HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<ApiRequestException>(() => response.EnsureApiSuccessAsync());
        Assert.Equal(JobFailureKind.Permanent, ex.FailureKind);
    }

    [Fact]
    public async Task ServerError_IsTransient()
    {
        using var response = Response(HttpStatusCode.ServiceUnavailable);
        var ex = await Assert.ThrowsAsync<ApiRequestException>(() => response.EnsureApiSuccessAsync());
        Assert.Equal(JobFailureKind.Transient, ex.FailureKind);
    }

    [Fact]
    public async Task TooManyRequests_CarriesRetryAfter()
    {
        using var response = Response(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "17");

        var ex = await Assert.ThrowsAsync<ApiRequestException>(() => response.EnsureApiSuccessAsync());

        Assert.Equal(JobFailureKind.Transient, ex.FailureKind);
        Assert.Equal(TimeSpan.FromSeconds(17), ex.RetryAfter);
    }

    [Fact]
    public async Task Message_ContainsProblemTitle_ButNeverTheQueryString()
    {
        using var response = Response(HttpStatusCode.BadRequest, """{"title":"Invalid continuation token"}""");

        var ex = await Assert.ThrowsAsync<ApiRequestException>(() => response.EnsureApiSuccessAsync());

        Assert.Contains("Invalid continuation token", ex.Message);
        Assert.Contains("/reservations", ex.Message);
        Assert.DoesNotContain("should-not-leak", ex.Message);
        Assert.DoesNotContain("apiKey", ex.Message);
    }
}

public class PageReaderTests
{
    [Fact]
    public async Task ReadsAllPages_InOrder()
    {
        var pages = new Dictionary<string, ContinuationPage<int>>
        {
            [""] = new([1, 2], "t2"),
            ["t2"] = new([3], "t3"),
            ["t3"] = new([4, 5], null),
        };

        var items = new List<int>();
        await foreach (var item in PageReader.ReadAllAsync<int>((token, _) =>
            Task.FromResult(pages[token ?? ""]), CancellationToken.None))
        {
            items.Add(item);
        }

        Assert.Equal([1, 2, 3, 4, 5], items);
    }

    [Fact]
    public async Task NonAdvancingToken_Throws_InsteadOfLoopingForever()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in PageReader.ReadAllAsync<int>((token, _) =>
                Task.FromResult(new ContinuationPage<int>([1], token ?? "same")), CancellationToken.None))
            {
            }
        });
    }
}
