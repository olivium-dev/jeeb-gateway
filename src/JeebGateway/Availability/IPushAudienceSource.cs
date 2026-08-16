namespace JeebGateway.Availability;

/// <summary>
/// OA-21 — the new-request push audience, read from the service that OWNS presence.
///
/// <para>THE DEFECT THIS REPLACES (durability register #9): the fan-out took its
/// audience from <see cref="IAvailabilityStore"/>, bound to
/// <c>InMemoryAvailabilityStore</c>. A gateway restart emptied it, so every
/// subsequent customer request fanned out to NOBODY — silently, and with no
/// self-heal, because each jeeber's own availability screen reads through to
/// delivery-service and still shows them online, so nobody re-toggles.</para>
///
/// <para>delivery-service already owns presence (the jeeber-facing GET/PATCH
/// availability calls forward to it), so the audience is ITS query to answer.
/// Nothing about a jeeb request travels on this hop: the two rungs are pure
/// presence questions — "who can work right now" and "who have we seen lately".</para>
///
/// <para>FAILURE IS NOT EMPTINESS. Every implementation MUST throw
/// <see cref="PushAudienceUnavailableException"/> when it cannot read the audience,
/// and MUST NOT substitute an empty list. "Nobody is available" and "we could not
/// ask" produce the same zero recipients but need opposite operator responses, and
/// collapsing them is exactly how register #9 stayed invisible for so long.</para>
///
/// <para>No auth on this hop by owner ruling — inter-service security is out of
/// scope. Do not add a token here.</para>
/// </summary>
public interface IPushAudienceSource
{
    /// <summary>
    /// Rung 1 — providers who can take work right now: online AND heartbeat-fresh
    /// AND with a GPS fix on file, per delivery-service's own freshness rule.
    /// Unfiltered by geography on purpose: the caller applies the request's tier
    /// radius itself so it can still report how far the nearest candidate was when
    /// the cut empties the set.
    /// </summary>
    /// <exception cref="PushAudienceUnavailableException">The audience could not be read.</exception>
    Task<IReadOnlyList<JeeberAvailability>> ListAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Rung 2 — the never-starve fallback: every provider seen since
    /// <paramref name="since"/>, ONLINE OR NOT. Rows are ever-was-a-jeeber, so the
    /// caller re-checks ActiveRole at send time exactly as it did before.
    /// </summary>
    /// <exception cref="PushAudienceUnavailableException">The audience could not be read.</exception>
    Task<IReadOnlyList<JeeberAvailability>> ListReachableSinceAsync(
        DateTimeOffset since, CancellationToken ct);
}

/// <summary>
/// The audience could not be read. Carries <see cref="Rung"/> so the fan-out's
/// error line names which read failed without the operator opening a trace.
/// </summary>
public sealed class PushAudienceUnavailableException : Exception
{
    public PushAudienceUnavailableException(string rung, Exception inner)
        : base($"The push audience rung '{rung}' could not be read from delivery-service.", inner)
        => Rung = rung;

    /// <summary>"available" or "reachable" — which of the two reads failed.</summary>
    public string Rung { get; }
}
