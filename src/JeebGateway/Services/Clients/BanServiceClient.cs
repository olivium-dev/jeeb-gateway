using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IBanServiceClient"/>.
///
/// Hand-coded (NOT NSwag-generated) against the verified routes on ban-service
/// (Rust / Actix-Web; <c>olivium-analysis/repos/ban-service</c>) and its live
/// OpenAPI 3.1 spec at <c>/api-docs/openapi.json</c>. Two reasons to hand-code,
/// matching the <c>NotificationServiceClient</c> precedent:
///   1. The wire is <b>snake_case</b> (<c>user_id</c>, <c>ban_statuses</c>,
///      <c>banned_until</c>, <c>is_currently_banned</c>, <c>current_stage</c>),
///      so an explicit <see cref="JsonNamingPolicy.SnakeCaseLower"/> policy plus
///      per-field <see cref="JsonPropertyNameAttribute"/> on the wire DTOs locks
///      the JSON seam (the same class of bug the DeliveryHandover contract test
///      guards against).
///   2. utoipa emits OpenAPI 3.1 <c>type: [..., "null"]</c> nullable arrays,
///      which NSwag 14.x handles inconsistently; a 12-line DTO is lower risk than
///      a generated client we would have to hand-patch anyway.
///
/// The named/typed HttpClient registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/> supplies
/// BaseAddress + the org-standard bearer / X-Service-Auth / resilience pipeline,
/// so this class never thinks about retry/timeout/circuit-breaker/auth.
/// </summary>
public sealed class BanServiceClient : IBanServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public BanServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct)
    {
        // GET /api/v1/ban/{userId}/status
        var url = $"api/v1/ban/{Uri.EscapeDataString(userId)}/status";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        RejectDuplicateProperties(document.RootElement);
        var wire = document.RootElement.Deserialize<WireStatuses>(JsonOptions);
        if (wire is null || wire.UserId != userId || wire.BanStatuses is null)
        {
            throw new HttpRequestException(
                "ban-service returned an invalid status envelope.");
        }

        return new BanStatusesResult
        {
            UserId = wire.UserId,
            BanStatuses = wire.BanStatuses
                .Select(status => MapStatus(status, userId))
                .ToList(),
        };
    }

    public async Task<BanStatusItem> ApplyBanAsync(string userId, string banType, CancellationToken ct)
    {
        // POST /api/v1/ban/{userId}/{banType} — no request body.
        var url = $"api/v1/ban/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(banType)}";
        using var response = await _http.PostAsync(url, content: null, ct);
        response.EnsureSuccessStatusCode();

        var wire = await response.Content.ReadFromJsonAsync<WireStatus>(JsonOptions, ct);
        if (wire is null)
        {
            throw new HttpRequestException(
                $"ban-service {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return MapStatus(wire, userId);
    }

    public async Task<BanStatusItem> ApplyTerminalBanAsync(
        string userId, string policyKey, CancellationToken ct)
    {
        var url = $"api/v1/ban/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(policyKey)}/terminal";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var wire = await response.Content.ReadFromJsonAsync<WireStatus>(JsonOptions, ct);
        if (wire is null)
        {
            throw new HttpRequestException(
                $"ban-service {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return MapStatus(wire, userId);
    }

    public async Task<BanResetResult> ForceResetAsync(string userId, CancellationToken ct)
    {
        var url = $"api/v1/ban/{Uri.EscapeDataString(userId)}/force-reset";
        using var response = await _http.PostAsync(url, content: null, ct);
        response.EnsureSuccessStatusCode();

        var wire = await response.Content.ReadFromJsonAsync<WireUpdate>(JsonOptions, ct);
        if (wire is null)
        {
            throw new HttpRequestException(
                $"ban-service {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return new BanResetResult
        {
            OldStatus = wire.OldStatus is null ? null : MapStatus(wire.OldStatus, userId),
            NewStatus = wire.NewStatus is null ? null : MapStatus(wire.NewStatus, userId),
            Updated = wire.Updated,
        };
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Match the serializer's case-insensitive names, including decoded
            // escapes. Never let a later property replace an earlier ban fact.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Ambiguous ban status response.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static BanStatusItem MapStatus(WireStatus? w, string expectedUserId)
    {
        if (w is null || w.UserId != expectedUserId
            || string.IsNullOrWhiteSpace(w.BanType)
            || string.IsNullOrWhiteSpace(w.Status) || w.CurrentStage < 0
            || w.LastUpdated == default)
            throw new HttpRequestException("ban-service returned an invalid status record.");
        return new BanStatusItem {
        UserId = w.UserId,
        BanType = w.BanType,
        CurrentStage = w.CurrentStage,
        Status = w.Status,
        Message = w.Message ?? string.Empty,
        BannedUntil = w.BannedUntil,
        LastUpdated = w.LastUpdated,
        IsCurrentlyBanned = w.IsCurrentlyBanned,
        };
    }

    // --- wire DTOs (snake_case as emitted by ban-service) ---

    private sealed class WireStatuses
    {
        [JsonRequired, JsonPropertyName("user_id")] public string? UserId { get; init; }
        [JsonRequired, JsonPropertyName("ban_statuses")] public List<WireStatus>? BanStatuses { get; init; }
    }

    private sealed class WireStatus
    {
        [JsonRequired, JsonPropertyName("user_id")] public string? UserId { get; init; }
        [JsonRequired, JsonPropertyName("ban_type")] public string? BanType { get; init; }
        [JsonRequired, JsonPropertyName("current_stage")] public int CurrentStage { get; init; }
        [JsonRequired, JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("banned_until")] public DateTimeOffset? BannedUntil { get; init; }
        [JsonRequired, JsonPropertyName("last_updated")] public DateTimeOffset LastUpdated { get; init; }
        [JsonRequired, JsonPropertyName("is_currently_banned")] public bool IsCurrentlyBanned { get; init; }
    }

    private sealed class WireUpdate
    {
        [JsonPropertyName("old_status")] public WireStatus? OldStatus { get; init; }
        [JsonPropertyName("new_status")] public WireStatus? NewStatus { get; init; }
        [JsonPropertyName("updated")] public bool Updated { get; init; }
    }
}
