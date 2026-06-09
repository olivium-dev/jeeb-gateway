using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Tracking;

/// <summary>
/// Resolves the two parties bound to a delivery (Client + Jeeber) and the
/// delivery's canonical status, for the S09 live-tracking + location-ingest
/// authorization gates.
///
/// <para>
/// This is a <b>gateway-side BFF composition</b>: the delivery-service owns the
/// canonical row (parties + SM-1 status); geolocation-service owns the GPS pings.
/// The two are NEVER joined in a downstream service — the gateway reads the
/// canonical delivery row to decide whether a caller may subscribe to / ingest
/// against a delivery, then (and only then) touches the geolocation surface.
/// This honours the org no-coupling law: delivery-service is the authority for
/// "who is a party" and "is the trip in_transit", and the gateway composes that
/// verdict with the geolocation stream.
/// </para>
///
/// <para>
/// Source of truth follows the same canonical-vs-mirror split already used by
/// <c>DeliveriesController.GetById</c>/<c>PatchStatus</c>: when
/// <c>FeatureFlags:UseUpstream:Delivery</c> is on, the canonical
/// <c>GET /api/v1/deliveries/{id}</c> row is authoritative; a transport blip
/// degrades to the local in-memory mirror so a tracking read never hard-fails.
/// When the flag is off, the local <see cref="IRequestsStore"/> mirror is used.
/// </para>
/// </summary>
public interface IDeliveryParticipantResolver
{
    /// <summary>
    /// Resolve the parties + canonical status of a delivery.
    /// Returns <c>null</c> when the delivery does not exist (caller maps to 404).
    /// </summary>
    Task<DeliveryParticipants?> ResolveAsync(string deliveryId, CancellationToken ct);
}

/// <summary>
/// The two parties bound to a delivery plus its canonical status, in the
/// canonical SM-1 vocab (Ordered/Picked/InTransit/AtDoor/Done) when the
/// upstream path is on, or the legacy snake_case mirror vocab when off.
/// <see cref="IsInTransit"/> normalises both vocabularies so the ingest
/// lifecycle gate is vocab-agnostic.
/// </summary>
public sealed class DeliveryParticipants
{
    public required string DeliveryId { get; init; }
    public string? ClientId { get; init; }
    public string? JeeberId { get; init; }
    public required string Status { get; init; }

    /// <summary>Dropoff point used to render the polyline tail (may be null).</summary>
    public GeoPoint? DropoffLocation { get; init; }

    /// <summary>
    /// True when <paramref name="userId"/> is the bound Client or Jeeber.
    /// Admins are handled by the caller (they get a participant-equivalent
    /// view for ops triage) — this method is party membership only.
    /// </summary>
    public bool IsParty(string userId) =>
        (!string.IsNullOrEmpty(ClientId) && string.Equals(ClientId, userId, StringComparison.Ordinal))
        || (!string.IsNullOrEmpty(JeeberId) && string.Equals(JeeberId, userId, StringComparison.Ordinal));

    /// <summary>
    /// True when the trip is in the en-route phase where live GPS ingest is
    /// accepted. Normalises the canonical <c>InTransit</c> and the legacy
    /// mirror <c>heading_off</c>/<c>in_transit</c> aliases so the ingest gate
    /// (N5/E4) is independent of which source answered.
    /// </summary>
    public bool IsInTransit =>
        string.Equals(Status, "InTransit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, RequestStatus.HeadingOff, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "in_transit", StringComparison.OrdinalIgnoreCase);
}

public sealed class DeliveryParticipantResolver : IDeliveryParticipantResolver
{
    private readonly IRequestsStore _requests;
    private readonly IDeliveryServiceClient _delivery;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<DeliveryParticipantResolver> _logger;

    public DeliveryParticipantResolver(
        IRequestsStore requests,
        IDeliveryServiceClient delivery,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<DeliveryParticipantResolver> logger)
    {
        _requests = requests;
        _delivery = delivery;
        _flags = flags;
        _logger = logger;
    }

    public async Task<DeliveryParticipants?> ResolveAsync(string deliveryId, CancellationToken ct)
    {
        // Canonical read-through (FeatureFlags:UseUpstream:Delivery): delivery-service
        // owns the parties + SM-1 status. A transport blip degrades to the local
        // mirror so the authz gate never hard-fails on a delivery-service hiccup.
        if (_flags.CurrentValue.Delivery)
        {
            try
            {
                var canonical = await _delivery.GetCanonicalDeliveryAsync(deliveryId, ct);
                if (canonical is null)
                {
                    return null;
                }

                // The canonical read doesn't carry the dropoff point; pull it
                // from the local mirror when present so the polyline tail still
                // renders. Best-effort — null dropoff just yields an empty tail.
                GeoPoint? dropoff = null;
                var mirror = await _requests.GetAsync(deliveryId, ct);
                if (mirror is not null)
                {
                    dropoff = mirror.DropoffLocation;
                }

                return new DeliveryParticipants
                {
                    DeliveryId = canonical.DeliveryId,
                    ClientId = canonical.ClientId,
                    JeeberId = canonical.JeeberId,
                    Status = canonical.Status,
                    DropoffLocation = dropoff,
                };
            }
            catch (HttpRequestException hre)
            {
                _logger.LogWarning(hre,
                    "Canonical participant read failed for delivery {DeliveryId}; falling back to the local mirror.",
                    deliveryId);
                // fall through to the local mirror
            }
        }

        var row = await _requests.GetAsync(deliveryId, ct);
        if (row is null)
        {
            return null;
        }

        return new DeliveryParticipants
        {
            DeliveryId = row.Id,
            ClientId = row.ClientId,
            JeeberId = row.JeeberId,
            Status = row.Status,
            DropoffLocation = row.DropoffLocation,
        };
    }
}
