namespace JeebGateway.Availability;

/// <summary>
/// Realtime fan-out for the "new offer" event (T-backend-010 acceptance
/// criterion: WS event to Client on new offer). The MVP gateway only owns
/// the contract — the in-memory implementation records calls so integration
/// tests can assert delivery without standing up a real WebSocket hub.
///
/// Production wiring will swap the in-memory variant for a SignalR /
/// dedicated realtime-service client that pushes the message down the
/// Client's open WS connection. Keeping the call site behind an interface
/// means the controller does not change when that swap lands.
/// </summary>
public interface IOfferRealtimeNotifier
{
    /// <summary>
    /// Fires once per new offer. Best-effort: failures must NOT roll back
    /// the submission — the offer is the source of truth, the realtime
    /// nudge is just a UX optimisation over the existing poll endpoint.
    /// </summary>
    Task NotifyNewOfferAsync(string clientId, PendingOffer offer, CancellationToken ct);
}

/// <summary>
/// MVP in-memory recorder used by tests and local dev. Captures every
/// dispatched event so integration tests can assert exactly one event
/// was emitted per accepted submission.
/// </summary>
public class InMemoryOfferRealtimeNotifier : IOfferRealtimeNotifier
{
    private readonly List<NewOfferEvent> _events = new();
    private readonly object _lock = new();

    public Task NotifyNewOfferAsync(string clientId, PendingOffer offer, CancellationToken ct)
    {
        lock (_lock)
        {
            _events.Add(new NewOfferEvent(
                ClientId: clientId,
                OfferId: offer.Id,
                RequestId: offer.RequestId,
                JeeberId: offer.JeeberId,
                Fee: offer.Fee,
                EtaMinutes: offer.EtaMinutes,
                Note: offer.Note,
                At: offer.CreatedAt));
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<NewOfferEvent> Events
    {
        get { lock (_lock) return _events.ToArray(); }
    }

    public sealed record NewOfferEvent(
        string ClientId,
        string OfferId,
        string RequestId,
        string JeeberId,
        decimal Fee,
        int EtaMinutes,
        string? Note,
        DateTimeOffset At);
}
