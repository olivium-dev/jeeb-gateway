using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Requests;

namespace JeebGateway.Services.Clients;

public interface ICaseDeliveryClient
{
    Task<DeliveryCaseContextUpstream?> GetDeliveryCaseContextAsync(
        string deliveryId, CancellationToken ct);

    Task<DeliveryTransitionUpstream> ActivateIncidentAsync(
        string deliveryId, string partySource, string actorId, string actorRole,
        string idempotencyKey, CancellationToken ct);
}

/// <summary>
/// Canonical delivery evidence and incident client for the case feature. The
/// registration is resilience-only and intentionally carries no auth handlers;
/// gateway edge authorization is completed before these private-network calls.
/// </summary>
public sealed class CaseDeliveryClient : ICaseDeliveryClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public CaseDeliveryClient(HttpClient http) => _http = http;

    public async Task<DeliveryCaseContextUpstream?> GetDeliveryCaseContextAsync(
        string deliveryId, CancellationToken ct)
    {
        var path = $"api/v1/deliveries/{Uri.EscapeDataString(deliveryId)}/status-history";
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<DeliveryCaseContextUpstream>(Json, ct)
            ?? throw new HttpRequestException("delivery-service returned an empty status-history response.");
        return string.IsNullOrWhiteSpace(value.DeliveryId)
            ? value with { DeliveryId = deliveryId }
            : value;
    }

    public async Task<DeliveryTransitionUpstream> ActivateIncidentAsync(
        string deliveryId, string partySource, string actorId, string actorRole,
        string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/v1/deliveries/{Uri.EscapeDataString(deliveryId)}/transition")
        {
            Content = JsonContent.Create(new IncidentRequest(
                CanonicalDeliveryStatus.FailedNeedsEscalation, partySource, idempotencyKey),
                options: Json),
        };
        request.Headers.TryAddWithoutValidation("X-Actor-ID", actorId);
        request.Headers.TryAddWithoutValidation("X-Actor-Role", actorRole);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<DeliveryTransitionUpstream>(Json, ct)
                ?? throw new HttpRequestException("delivery-service returned an empty transition response.");
        }

        TransitionProblem? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<TransitionProblem>(Json, ct);
        }
        catch (JsonException)
        {
            // Preserve the status even when a proxy returns a non-JSON body.
        }
        throw new DeliveryTransitionException((int)response.StatusCode,
            problem?.Reason, problem?.From, problem?.To, problem?.Trigger);
    }

    private sealed record IncidentRequest(
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("trigger")] string Trigger,
        [property: JsonPropertyName("idempotency_key")] string IdempotencyKey);

    private sealed record TransitionProblem(
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("from")] string? From,
        [property: JsonPropertyName("to")] string? To,
        [property: JsonPropertyName("trigger")] string? Trigger);
}

public sealed record DeliveryCaseContextUpstream
{
    [JsonPropertyName("delivery_id")]
    public string DeliveryId { get; init; } = string.Empty;

    [JsonPropertyName("party_ids")]
    public required DeliveryCasePartyIdsUpstream PartyIds { get; init; }

    [JsonPropertyName("current_status")]
    public required string CurrentStatus { get; init; }

    [JsonPropertyName("status_history")]
    public IReadOnlyList<DeliveryHistoryEntryUpstream> StatusHistory { get; init; }
        = Array.Empty<DeliveryHistoryEntryUpstream>();
}

public sealed class DeliveryCasePartyIdsUpstream
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("courier_id")]
    public string? CourierId { get; init; }
}

public sealed class DeliveryHistoryEntryUpstream
{
    [JsonPropertyName("transition_id")]
    public string? TransitionId { get; init; }

    [JsonPropertyName("from_status")]
    public string? FromStatus { get; init; }

    [JsonPropertyName("to_status")]
    public required string ToStatus { get; init; }

    [JsonPropertyName("trigger")]
    public string? Trigger { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("actor_id")]
    public string? ActorId { get; init; }

    [JsonPropertyName("evidence_url")]
    public string? EvidenceUrl { get; init; }

    [JsonPropertyName("geo")]
    public JsonElement? Geo { get; init; }

    [JsonPropertyName("transitioned_at")]
    public DateTimeOffset TransitionedAt { get; init; }
}
