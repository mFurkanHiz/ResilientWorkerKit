using System.Collections.Concurrent;

namespace ReservationReconciliation.Worker.Domain;

/// <summary>
/// The sample's "system of record". Every <see cref="Reconcile"/> call counts as one side
/// effect, so the logs make duplicate suppression visible: a reservation version that was
/// already reconciled (idempotency record exists) never increments the counter again —
/// even across host restarts.
/// </summary>
public sealed class ReservationLedger
{
    private readonly ConcurrentDictionary<int, Reservation> _reservations = new();
    private int _sideEffectCount;

    /// <summary>Total side effects performed (used to demonstrate idempotency).</summary>
    public int SideEffectCount => _sideEffectCount;

    /// <summary>All reconciled reservations.</summary>
    public IReadOnlyCollection<Reservation> Reservations => _reservations.Values.ToList();

    /// <summary>Applies one reservation to the ledger. Each call is one side effect.</summary>
    public void Reconcile(Reservation reservation)
    {
        Interlocked.Increment(ref _sideEffectCount);
        _reservations[reservation.Id] = reservation;
    }
}
