using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Tracking;

namespace JeebGateway.Services.Clients;

public sealed class GeolocationServiceClient : IGeolocationServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public GeolocationServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<LocationUpdateResponse> UpdateLocationAsync(string jeeberId, LocationUpdateRequest body, CancellationToken ct)
    {
        // JEB-1485: geolocation-service is now generic. The batch heartbeat
        // ingest is the product-agnostic POST /location/update, which derives
        // the principal from the forwarded bearer (no id in the path). jeeberId
        // is retained on the gateway-side signature for tracing/compat.
        _ = jeeberId;
        const string path = "location/update";
        using var response = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException("geolocation-service returned an empty body.");
        }
        return payload;
    }

    public async Task<Stream> OpenTrackingStreamAsync(string deliveryId, TrackingStreamQuery query, CancellationToken ct)
    {
        // JEB-1485: the shared service exposes an opaque, generic per-track SSE
        // stream: GET /v1/geo/tracks/{trackId}/tracking/stream. The Jeeb delivery
        // id maps onto the generic trackId; all participant + in_transit lifecycle
        // gating is enforced in the gateway (LocationController) BEFORE this
        // upstream subscription is opened. The previous dropoff_*/jeeber_id query
        // hints (a hand-rolled extension the generic service does not model) are
        // dropped — the gateway already owns dropoff/polyline projection.
        _ = query;
        var url = $"v1/geo/tracks/{Uri.EscapeDataString(deliveryId)}/tracking/stream";

        var message = new HttpRequestMessage(HttpMethod.Get, url);
        // ResponseHeadersRead avoids buffering the SSE stream — we hand the
        // open response Stream straight back to the controller for forwarding.
        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<IReadOnlyList<double[]>> GetRoutePolylineAsync(string deliveryId, CancellationToken ct)
    {
        var path = $"locations/{Uri.EscapeDataString(deliveryId)}/route";
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Array.Empty<double[]>();
        }
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<IReadOnlyList<double[]>>(JsonOptions, ct);
        return result ?? Array.Empty<double[]>();
    }
}
