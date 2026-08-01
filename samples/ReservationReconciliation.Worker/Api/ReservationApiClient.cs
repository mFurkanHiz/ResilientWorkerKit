using System.Net.Http.Json;
using ReservationReconciliation.Worker.Domain;
using ResilientWorkerKit.Http;

namespace ReservationReconciliation.Worker.Api;

/// <summary>Typed client for the (fake) reservation API.</summary>
public interface IReservationApiClient
{
    /// <summary>Fetches one page of reservations.</summary>
    Task<ContinuationPage<Reservation>> GetReservationsAsync(string? continuationToken, CancellationToken cancellationToken);

    /// <summary>Sends an outbound notification (idempotency-key header added by the pipeline).</summary>
    Task SendNotificationAsync(string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>
/// HttpClient-based implementation. All resilience (retry on 5xx, Retry-After on 429,
/// timeouts, circuit breaker), correlation and idempotency-key headers and safe logging come
/// from the <c>AddResilientApiClient</c> pipeline — this class only does happy-path I/O.
/// </summary>
public sealed class ReservationApiClient : IReservationApiClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Creates the client.</summary>
    public ReservationApiClient(HttpClient httpClient) => _httpClient = httpClient;

    /// <inheritdoc />
    public async Task<ContinuationPage<Reservation>> GetReservationsAsync(string? continuationToken, CancellationToken cancellationToken)
    {
        var uri = string.IsNullOrEmpty(continuationToken)
            ? "reservations"
            : $"reservations?continuationToken={Uri.EscapeDataString(continuationToken)}";

        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        await response.EnsureApiSuccessAsync(cancellationToken);

        var page = await response.Content.ReadFromJsonAsync<ReservationPage>(cancellationToken)
            ?? throw new ApiRequestException("The reservation API returned an empty body.", response.StatusCode);

        return new ContinuationPage<Reservation>(page.Items, page.NextContinuationToken);
    }

    /// <inheritdoc />
    public async Task SendNotificationAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "notifications")
        {
            Content = JsonContent.Create(new { kind = "reservation-status" }),
        };
        request.WithIdempotencyKey(idempotencyKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync(cancellationToken);
    }
}
