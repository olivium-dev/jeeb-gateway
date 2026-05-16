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
        var path = $"jeeb/jeebers/{Uri.EscapeDataString(jeeberId)}/location/update";
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
        var url = $"jeeb/deliveries/{Uri.EscapeDataString(deliveryId)}/tracking";
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(query.JeeberId)) qs.Add($"jeeber_id={Uri.EscapeDataString(query.JeeberId)}");
        if (query.DropoffLat.HasValue) qs.Add($"dropoff_lat={query.DropoffLat.Value}");
        if (query.DropoffLng.HasValue) qs.Add($"dropoff_lng={query.DropoffLng.Value}");
        if (qs.Count > 0) url += "?" + string.Join('&', qs);

        var message = new HttpRequestMessage(HttpMethod.Get, url);
        // ResponseHeadersRead avoids buffering the SSE stream — we hand the
        // open response Stream straight back to the controller for forwarding.
        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }
}
