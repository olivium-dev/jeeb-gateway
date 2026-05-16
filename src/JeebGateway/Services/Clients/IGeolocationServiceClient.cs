using JeebGateway.Tracking;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over geolocation-service (Python FastAPI). Two endpoints:
///  - POST jeeb/jeebers/{id}/location/update — batched GPS ingest.
///  - GET  jeeb/deliveries/{id}/tracking — SSE stream (returns a raw
///    <see cref="Stream"/> so the controller can re-emit framed bytes
///    without buffering the entire stream).
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
