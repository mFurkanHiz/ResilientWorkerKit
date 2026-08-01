using ReservationReconciliation.Worker.Domain;

namespace ReservationReconciliation.Worker.Api;

/// <summary>Minimal-API endpoints of the embedded fake reservation API.</summary>
public static class FakeReservationApi
{
    /// <summary>Maps the fake API under <c>/fake-api</c>.</summary>
    public static WebApplication MapFakeReservationApi(this WebApplication app)
    {
        var group = app.MapGroup("/fake-api");

        group.MapGet("/reservations", (string? continuationToken, FakeReservationApiState state, HttpResponse response) =>
        {
            switch (continuationToken)
            {
                case "page-2" when state.RegisterPage2Call() <= 2:
                    // First two calls to page 2 fail — demonstrates transparent HTTP retry.
                    return Results.Problem(
                        title: "Simulated upstream outage",
                        statusCode: StatusCodes.Status500InternalServerError);

                case "page-3" when state.RegisterPage3Call() == 1:
                    // First call to page 3 is rate-limited — demonstrates Retry-After handling.
                    response.Headers.RetryAfter = "1";
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);

                default:
                    return Results.Ok(FakeReservationApiState.GetPage(continuationToken));
            }
        });

        group.MapPost("/notifications", (HttpRequest request, NotificationInbox inbox, ILogger<NotificationInbox> logger) =>
        {
            var idempotencyKey = request.Headers["Idempotency-Key"].ToString();
            var correlationId = request.Headers["X-Correlation-ID"].ToString();
            inbox.Register(idempotencyKey);
            logger.LogInformation(
                "Fake API received notification (idempotencyKey={IdempotencyKey}, correlation={CorrelationId})",
                idempotencyKey, correlationId);
            return Results.Accepted();
        });

        return app;
    }
}

/// <summary>Collects notifications received by the fake API (for the demo output).</summary>
public sealed class NotificationInbox
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private int _received;

    /// <summary>Total notifications received.</summary>
    public int Received => _received;

    /// <summary>Registers a delivery.</summary>
    public void Register(string idempotencyKey)
    {
        Interlocked.Increment(ref _received);
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            lock (_keys)
            {
                _keys.Add(idempotencyKey);
            }
        }
    }
}
