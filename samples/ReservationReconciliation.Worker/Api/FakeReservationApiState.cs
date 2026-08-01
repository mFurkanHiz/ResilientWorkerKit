using ReservationReconciliation.Worker.Domain;

namespace ReservationReconciliation.Worker.Api;

/// <summary>
/// Scripted behavior of the fake reservation API:
/// <list type="bullet">
/// <item>Page 1 (no token) always succeeds.</item>
/// <item>Page 2 fails with HTTP 500 on its first two calls, then succeeds — the resilience
/// pipeline retries transparently.</item>
/// <item>Page 3 answers HTTP 429 with <c>Retry-After: 1</c> on its first call, then succeeds.</item>
/// <item>Reservation 104 has an invalid payload (negative nights) — the job dead-letters it
/// as a permanent per-item failure and moves on.</item>
/// <item>Reservation 101 appears again on page 3 with the same version — idempotency
/// suppresses the second side effect.</item>
/// </list>
/// </summary>
public sealed class FakeReservationApiState
{
    private int _page2Calls;
    private int _page3Calls;

    /// <summary>Number of calls made to page 2 so far.</summary>
    public int RegisterPage2Call() => Interlocked.Increment(ref _page2Calls);

    /// <summary>Number of calls made to page 3 so far.</summary>
    public int RegisterPage3Call() => Interlocked.Increment(ref _page3Calls);

    /// <summary>The scripted pages.</summary>
    public static ReservationPage GetPage(string? continuationToken) => continuationToken switch
    {
        null or "" => new ReservationPage(
            [
                new Reservation(101, 1, "A-101", 2, "confirmed"),
                new Reservation(102, 1, "A-102", 1, "confirmed"),
                new Reservation(103, 2, "B-201", 3, "modified"),
            ],
            "page-2"),
        "page-2" => new ReservationPage(
            [
                new Reservation(104, 1, "B-202", -1, "corrupted"), // invalid: negative nights
                new Reservation(105, 1, "C-301", 4, "confirmed"),
            ],
            "page-3"),
        "page-3" => new ReservationPage(
            [
                new Reservation(101, 1, "A-101", 2, "confirmed"), // duplicate of page 1
                new Reservation(106, 1, "C-302", 1, "cancelled"),
            ],
            null),
        _ => new ReservationPage([], null),
    };
}
