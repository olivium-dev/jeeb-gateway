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
/// matching the <see cref="NotificationServiceClient"/> precedent:
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

        var wire = await response.Content.ReadFromJsonAsync<WireStatuses>(JsonOptions, ct);
        if (wire is null)
        {
            throw new HttpRequestException(
                $"ban-service {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return new BanStatusesResult
        {
            UserId = wire.UserId ?? userId,
            BanStatuses = (wire.BanStatuses ?? new List<WireStatus>())
                .Select(MapStatus)
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

        return MapStatus(wire);
    }

    private static BanStatusItem MapStatus(WireStatus w) => new()
    {
        UserId = w.UserId ?? string.Empty,
        BanType = w.BanType ?? string.Empty,
        CurrentStage = w.CurrentStage,
        Status = w.Status ?? string.Empty,
        Message = w.Message ?? string.Empty,
        BannedUntil = w.BannedUntil,
        LastUpdated = w.LastUpdated,
        IsCurrentlyBanned = w.IsCurrentlyBanned,
    };

    // --- wire DTOs (snake_case as emitted by ban-service) ---

    private sealed class WireStatuses
    {
        [JsonPropertyName("user_id")] public string? UserId { get; init; }
        [JsonPropertyName("ban_statuses")] public List<WireStatus>? BanStatuses { get; init; }
    }

    private sealed class WireStatus
    {
        [JsonPropertyName("user_id")] public string? UserId { get; init; }
        [JsonPropertyName("ban_type")] public string? BanType { get; init; }
        [JsonPropertyName("current_stage")] public int CurrentStage { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("banned_until")] public DateTimeOffset? BannedUntil { get; init; }
        [JsonPropertyName("last_updated")] public DateTimeOffset LastUpdated { get; init; }
        [JsonPropertyName("is_currently_banned")] public bool IsCurrentlyBanned { get; init; }
    }
}
