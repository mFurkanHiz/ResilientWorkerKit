namespace ReservationReconciliation.Worker.Domain;

/// <summary>
/// A fully fictional reservation used by the sample. Deliberately contains no personal data —
/// only codes and counters.
/// </summary>
public sealed record Reservation(int Id, int Version, string RoomCode, int Nights, string Status);

/// <summary>One page of reservations returned by the fake API.</summary>
public sealed record ReservationPage(IReadOnlyList<Reservation> Items, string? NextContinuationToken);
