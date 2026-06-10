using JeebGateway.Tracking;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the SHARED, product-agnostic geolocation-service
/// (Python FastAPI). The upstream contract is generic (JEB-1485):
///  - POST v1/geo/location/... batched GPS ingest via the generic
///    POST /location/update (principal derived from the forwarded bearer).
///  - GET  v1/geo/tracks/{trackId}/tracking/stream — opaque per-track SSE
///    stream (returns a raw <see cref="Stream"/> so the controller can
///    re-emit framed bytes without buffering the entire stream).
///
/// Jeeb-domain semantics (delivery participants, in_transit lifecycle gating,
/// dropoff/polyline projection) live in the gateway (LocationController) and
/// are enforced BEFORE any upstream subscription. NSwag-typed-client
/// regeneration from contracts/geolocation-service.openapi.json is GR4 debt
/// (see scripts/regenerate-clients.sh) — deferred to CI/owner.
/// </summary>
public interface IGeolocationServiceClient
{
    Task<LocationUpdateResponse> UpdateLocationAsync(string jeeberId, LocationUpdateRequest body, CancellationToken ct);

    Task<Stream> OpenTrackingStreamAsync(string deliveryId, TrackingStreamQuery query, CancellationToken ct);
}

public sealed class TrackingStreamQuery
{
    public string? JeeberId { get; init; }
    public double? DropoffLat { get; init; }
    public double? DropoffLng { get; init; }
}
