using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IPushNotificationClient"/>.
/// Hand-coded against the published route on the push-notification FastAPI
/// service (<c>app/endpoints/register_user.py</c>) pending an NSwag spec we
/// can generate from — the committed
/// <c>contracts/push-notification.openapi.json</c> is still a placeholder.
///
/// The named HttpClient ("push-notification" / typed registration bound to
/// <c>Services:PushNotification</c>) supplies BaseAddress + the org-standard
/// resilience pipeline from <see cref="Extensions.ServiceClientExtensions"/>;
/// this class never has to think about retry/timeout/circuit-breaker.
/// </summary>
public sealed class PushNotificationClient : IPushNotificationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public PushNotificationClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// PUT /api/v1/register — atomic upsert into the <c>push_notification</c>
    /// table keyed on (device_id, user_id). Returns 201 with a {message} body
    /// on success; non-2xx throws via EnsureSuccessStatusCode.
    /// </summary>
    public async Task RegisterDeviceAsync(RegisterDeviceUpstreamRequest request, CancellationToken ct)
    {
        var payload = new RegisterDeviceWire
        {
            UserId = request.UserId,
            FcmToken = request.FcmToken,
            DeviceId = request.DeviceId,
            ActiveRole = request.ActiveRole
        };

        // BaseAddress carries a trailing slash (see ServiceClientExtensions.BindBaseAddress),
        // so the relative "api/v1/register" resolves under the configured host.
        using var response = await _http.PutAsJsonAsync("api/v1/register", payload, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Wire shape for the upstream <c>RegisterRequest</c> pydantic model.
    /// snake_case names match the FastAPI schema exactly; <c>active_role</c>
    /// is omitted from the payload when null (upstream optional field).
    /// </summary>
    private sealed class RegisterDeviceWire
    {
        [JsonPropertyName("user_id")]
        public required string UserId { get; init; }

        [JsonPropertyName("fcm_token")]
        public required string FcmToken { get; init; }

        [JsonPropertyName("device_id")]
        public required string DeviceId { get; init; }

        [JsonPropertyName("active_role")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ActiveRole { get; init; }
    }
}
