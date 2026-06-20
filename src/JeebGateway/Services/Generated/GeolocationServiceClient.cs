// PENDING CI REGEN
//----------------------
// Hand-authored stand-in for the NSwag-generated geolocation-service client.
//
// The canonical Olivium BFF pattern is to regenerate this file from the pinned
// spec at contracts/geolocation-service.openapi.json via scripts/regenerate-clients.sh
// (registry row: geolocation-service ->
//  JeebGateway.Services.Generated.GeolocationService / GeolocationServiceClient.cs).
//
// It is hand-authored here for the same two reasons NotificationServiceClient /
// BanServiceClient are hand-coded (see those files): (1) geolocation-service is a
// FastAPI/Pydantic service whose wire is strict snake_case (user_id / lat / lng /
// accuracy / timestamp / distance_km / created_at), so an explicit
// JsonNamingPolicy.SnakeCaseLower seam locks the contract the same way the
// integration test asserts it; (2) the spec is OpenAPI 3.1 with `type: [..., "null"]`
// nullable unions that NSwag 14.x lowers inconsistently. The shape, namespace,
// partial-class layout, injected-HttpClient ctor, and `ApiException` mirror the
// NSwag output (ServiceRemoteUserPreferencesClient.cs) so a future CI regen is a
// drop-in replacement with no call-site churn.
//
// Routes covered (the gateway's tracking surface only — NOT the whole spec):
//   * POST /location/update            -> LocationUpdateResponse
//   * GET  /locations/user/{user_id}   -> UserLocationResponse (404 => null)
//   * GET  /locations/nearest          -> LocationWithDistance[]
//----------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Services.Generated.GeolocationService
{
    /// <summary>
    /// Typed proxy over the shared, product-agnostic geolocation-service
    /// (Python / FastAPI). Mirrors the NSwag-generated client surface but with a
    /// snake_case JSON seam (see file banner). The named/typed
    /// <see cref="HttpClient"/> registered in
    /// <c>JeebGateway.Extensions.ServiceClientExtensions</c> supplies BaseAddress
    /// plus the org-standard bearer / X-Service-Auth / resilience pipeline, so this
    /// class never thinks about retry/timeout/circuit-breaker/auth.
    /// </summary>
    public interface IGeolocationServiceClient
    {
        /// <summary>POST /location/update — batched GPS ingest. The principal is
        /// derived upstream from the forwarded bearer; the body carries only points.</summary>
        Task<LocationUpdateResponse> UpdateLocationAsync(LocationUpdateRequest body, CancellationToken cancellationToken = default);

        /// <summary>GET /locations/user/{user_id} — the latest stored fix for a user.
        /// Returns <c>null</c> on a 404 (no fix on record), not an exception.</summary>
        Task<UserLocationResponse?> GetUserLocationAsync(string userId, CancellationToken cancellationToken = default);

        /// <summary>GET /locations/nearest — nearest stored fixes to a point, ordered
        /// by distance. <paramref name="userId"/> is an optional exclusion/scoping filter.</summary>
        Task<IReadOnlyList<LocationWithDistance>> GetNearestAsync(double latitude, double longitude, int? limit = null, string? userId = null, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// See <see cref="IGeolocationServiceClient"/>. Declared <c>partial</c> to match
    /// the NSwag layout (a future regen can add the remaining operations without
    /// disturbing the hand-authored seam).
    /// </summary>
    public partial class GeolocationServiceClient : IGeolocationServiceClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly HttpClient _httpClient;

        public GeolocationServiceClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<LocationUpdateResponse> UpdateLocationAsync(LocationUpdateRequest body, CancellationToken cancellationToken = default)
        {
            if (body is null) throw new ArgumentNullException(nameof(body));

            // Operation Path: "location/update"
            using var response = await _httpClient
                .PostAsJsonAsync("location/update", body, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var payload = await response.Content
                .ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return payload ?? throw new ApiException(
                "geolocation-service returned an empty body for POST /location/update.",
                (int)response.StatusCode, null, null);
        }

        public async Task<UserLocationResponse?> GetUserLocationAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (userId is null) throw new ArgumentNullException(nameof(userId));

            // Operation Path: "locations/user/{user_id}"
            var url = "locations/user/" + Uri.EscapeDataString(userId);
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // 404 == "no fix on record", which the store maps to null (no exception).
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            return await response.Content
                .ReadFromJsonAsync<UserLocationResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<LocationWithDistance>> GetNearestAsync(double latitude, double longitude, int? limit = null, string? userId = null, CancellationToken cancellationToken = default)
        {
            // Operation Path: "locations/nearest"
            var urlBuilder = new StringBuilder("locations/nearest?");
            Append(urlBuilder, "latitude", latitude.ToString("R", CultureInfo.InvariantCulture));
            Append(urlBuilder, "longitude", longitude.ToString("R", CultureInfo.InvariantCulture));
            if (limit is not null)
            {
                Append(urlBuilder, "limit", limit.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (userId is not null)
            {
                Append(urlBuilder, "user_id", userId);
            }
            urlBuilder.Length--; // trim trailing '&'

            using var response = await _httpClient.GetAsync(urlBuilder.ToString(), cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var payload = await response.Content
                .ReadFromJsonAsync<List<LocationWithDistance>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return payload ?? new List<LocationWithDistance>();
        }

        private static void Append(StringBuilder builder, string key, string value)
        {
            builder.Append(Uri.EscapeDataString(key))
                   .Append('=')
                   .Append(Uri.EscapeDataString(value))
                   .Append('&');
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            throw new ApiException(
                $"geolocation-service returned an unexpected status ({(int)response.StatusCode}) for {response.RequestMessage?.RequestUri}.",
                (int)response.StatusCode, body, null);
        }
    }

    // ----- wire DTOs (snake_case as emitted by geolocation-service) -----
    // Property names map to the spec via JsonPropertyName so the seam is explicit
    // even if a future serializer option diverges from SnakeCaseLower.

    /// <summary>POST /location/update body. <c>{ "points": [ ... ] }</c>.</summary>
    public partial class LocationUpdateRequest
    {
        [JsonPropertyName("points")]
        public List<GpsBatchPoint> Points { get; set; } = new();
    }

    /// <summary>A single GPS sample on the outbound batch. snake_case on the wire:
    /// <c>lat / lng / accuracy / timestamp</c>.</summary>
    public partial class GpsBatchPoint
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lng")]
        public double Lng { get; set; }

        [JsonPropertyName("accuracy")]
        public double? Accuracy { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset? Timestamp { get; set; }
    }

    /// <summary>POST /location/update response.</summary>
    public partial class LocationUpdateResponse
    {
        [JsonPropertyName("accepted")]
        public int Accepted { get; set; }

        [JsonPropertyName("rejected")]
        public int Rejected { get; set; }

        [JsonPropertyName("online")]
        public bool Online { get; set; }

        [JsonPropertyName("latest")]
        public GpsBatchLatest? Latest { get; set; }
    }

    /// <summary>The latest-known fix echoed on the update response.</summary>
    public partial class GpsBatchLatest
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lng")]
        public double Lng { get; set; }

        [JsonPropertyName("accuracy")]
        public double? Accuracy { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset? Timestamp { get; set; }
    }

    /// <summary>GET /locations/user/{user_id} response.</summary>
    public partial class UserLocationResponse
    {
        [JsonPropertyName("user_id")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }
    }

    /// <summary>GET /locations/nearest array item.</summary>
    public partial class LocationWithDistance
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("user_id")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }

        [JsonPropertyName("distance_km")]
        public double DistanceKm { get; set; }
    }

    /// <summary>
    /// Mirrors the NSwag-generated <c>ApiException</c> so a future regen keeps the
    /// same failure type. Non-success upstream statuses surface as this.
    /// </summary>
    public partial class ApiException : Exception
    {
        public int StatusCode { get; }
        public string? Response { get; }
        public IReadOnlyDictionary<string, IEnumerable<string>>? Headers { get; }

        public ApiException(string message, int statusCode, string? response, IReadOnlyDictionary<string, IEnumerable<string>>? headers)
            : base(message + "\n\nStatus: " + statusCode + "\nResponse: \n" + (response ?? string.Empty))
        {
            StatusCode = statusCode;
            Response = response;
            Headers = headers;
        }
    }
}
