using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Tiers;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IDeliveryServiceClient"/>.
/// Targets the routes in delivery-service main (internal/jeeb/handlers.go).
/// </summary>
public sealed class DeliveryServiceClient : IDeliveryServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DeliveryServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/tiers", ct);
        response.EnsureSuccessStatusCode();
        // Upstream returns a raw JSON array — not wrapped in an envelope.
        return await DeserializeAsync<IReadOnlyList<DeliveryTierDto>>(response, ct);
    }

    /// <inheritdoc />
    public async Task<ShipmentsListDto> ListShipmentsAsync(
        string? orderId,
        string? stage,
        int? limit,
        CancellationToken ct)
    {
        // Build query string from optional filters — only append params that
        // are provided so the delivery-service default behaviour applies when
        // a filter is absent.
        var qs = new System.Text.StringBuilder("api/v1/shipments");
        var sep = '?';

        if (!string.IsNullOrWhiteSpace(orderId))
        {
            qs.Append(sep).Append("orderId=").Append(Uri.EscapeDataString(orderId));
            sep = '&';
        }
        if (!string.IsNullOrWhiteSpace(stage))
        {
            qs.Append(sep).Append("stage=").Append(Uri.EscapeDataString(stage));
            sep = '&';
        }
        if (limit is > 0)
        {
            qs.Append(sep).Append("limit=").Append(limit.Value);
        }

        using var response = await _http.GetAsync(qs.ToString(), ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<ShipmentsListDto>(response, ct);
    }

    public async Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("jeeb/requests", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<DeliveryRequestUpstream>(response, ct);
    }

    public async Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"jeeb/deliveries/{Uri.EscapeDataString(deliveryId)}", ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<DeliveryRequestUpstream>(response, ct);
    }

    public async Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"jeeb/deliveries/{Uri.EscapeDataString(deliveryId)}/verify-otp",
            new { otpCode },
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<DeliveryOtpVerifyResult>(response, ct);
    }

    public async Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
    {
        // T-BE-019 (JEB-55): upstream's PATCH /jeeb/deliveries/{id}/status is
        // the canonical state-machine writer. The gateway hands off the
        // transition so commission settlement (T-BE-020) keys off the
        // source-of-truth record rather than the gateway's read-cache.
        using var response = await _http.PatchAsync(
            $"jeeb/deliveries/{Uri.EscapeDataString(deliveryId)}/status",
            JsonContent.Create(new { status }, options: JsonOptions),
            ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<DeliveryRequestUpstream>(response, ct);
    }

    public async Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct)
    {
        // Frozen contract: POST /api/v1/deliveries/{id}/otp/issue
        //   200 -> { delivery_id, issued:true }
        //   409 -> { reason:"not_at_door" } | 404
        // body carries only an OPTIONAL code_hash for support — never the raw
        // code (AC5). When null we still send a (null) field; STJ omits it
        // under web defaults only if we drop it, so we send an explicit object.
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/deliveries/{Uri.EscapeDataString(deliveryId)}/otp/issue",
            new HandoverIssueRequest(codeHash),
            JsonOptions,
            ct);

        if (response.IsSuccessStatusCode)
        {
            return await DeserializeAsync<DeliveryHandoverIssueResult>(response, ct);
        }

        var reason = await TryReadReasonAsync(response, ct);
        throw new DeliveryHandoverException((int)response.StatusCode, reason);
    }

    public async Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, CancellationToken ct)
    {
        // Frozen contract: POST /api/v1/deliveries/{id}/otp/verify
        //   body { success:bool }  (NO raw code — AC5)
        //   200 -> { delivery_id, verified:true, status:"Done" }
        //   401 -> { reason:"invalid_code", attempts_remaining }
        //   423 -> { reason:"locked", escalation_id }
        //   409 -> { reason:"not_at_door" } | 404
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/deliveries/{Uri.EscapeDataString(deliveryId)}/otp/verify",
            new HandoverVerifyRequest(success),
            JsonOptions,
            ct);

        if (response.IsSuccessStatusCode)
        {
            return await DeserializeAsync<DeliveryHandoverVerifyResult>(response, ct);
        }

        var problem = await TryReadHandoverProblemAsync(response, ct);
        throw new DeliveryHandoverException(
            (int)response.StatusCode,
            problem?.Reason,
            problem?.AttemptsRemaining,
            problem?.EscalationId,
            problem?.LockedAt);
    }

    public async Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"jeeb/deliveries/{Uri.EscapeDataString(deliveryId)}/cancel",
            body,
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<DeliveryCancelResult>(response, ct);
    }

    public async Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct)
    {
        // S06 presence wire. Canonical route carries the jeeber id IN THE PATH
        // (POST /api/v1/jeebers/{id}/availability) — the same store the matching
        // run reads its online set from. Body is snake_case (see the DTO's
        // [JsonPropertyName] attributes). Replaces the never-existing
        // jeeb/jeebers/me/availability + X-User-Id-header shape.
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/jeebers/{Uri.EscapeDataString(jeeberId)}/availability",
            body,
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<JeeberAvailabilityUpstream>(response, ct);
    }

    /// <inheritdoc />
    public async Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct)
    {
        // S06 presence read. GET /api/v1/jeebers/{id}/availability — read-only,
        // never mutates. A 404 (no presence row yet) maps to null so the
        // controller can return a never-online default instead of a 500.
        using var response = await _http.GetAsync(
            $"api/v1/jeebers/{Uri.EscapeDataString(jeeberId)}/availability",
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<JeeberAvailabilityUpstream>(response, ct);
    }

    /// <inheritdoc />
    public async Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct)
    {
        // S06 GPS heartbeat wire. POST /api/v1/jeebers/{id}/heartbeat bumps
        // last_heartbeat_at + last-known location in the SAME presence store the
        // matching run reads for freshness. Body { lat, lng } (snake_case-clean —
        // both keys are already lowercase under the web-default policy).
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/jeebers/{Uri.EscapeDataString(jeeberId)}/heartbeat",
            new HeartbeatRequest(lat, lng),
            JsonOptions,
            ct);

        if (response.IsSuccessStatusCode)
        {
            return await DeserializeAsync<JeeberAvailabilityUpstream>(response, ct);
        }

        // 404 (jeeber never went online) and any other non-2xx surface as a typed
        // presence exception so the controller maps them to a non-500
        // ProblemDetails rather than leaking an unhandled 500.
        var reason = await TryReadReasonAsync(response, ct);
        throw new DeliveryAvailabilityException((int)response.StatusCode, reason);
    }

    /// <inheritdoc />
    public async Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct)
    {
        // Courier matching relocated to delivery-service (Go). Canonical route:
        //   POST /api/v1/matching/run
        // Request + response are snake_case (Go). The request DTO carries
        // explicit [JsonPropertyName] so the camelCase web-default policy on the
        // shared JsonOptions does not mis-serialize request_id / pickup_lat /
        // allowed_vehicle_types onto the Go field names. The response DTOs do the
        // same on the bind side (see DeliveryMatchingRunResult).
        using var response = await _http.PostAsJsonAsync("api/v1/matching/run", body, JsonOptions, ct);

        if (response.IsSuccessStatusCode)
        {
            return await DeserializeAsync<DeliveryMatchingRunResult>(response, ct);
        }

        // 400/404/422 — surface the upstream status + reason so the controller
        // can map straight through to RFC 7807 ProblemDetails (the gateway is a
        // thin BFF on this path; it does not re-run matching logic).
        var reason = await TryReadReasonAsync(response, ct);
        throw new DeliveryMatchingException((int)response.StatusCode, reason);
    }

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException($"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }
        return payload;
    }

    /// <summary>
    /// Reads the <c>reason</c> field off a non-200 handover response, tolerating
    /// a missing/non-JSON body (a proxy 5xx page) — returns null in that case.
    /// </summary>
    private static async Task<string?> TryReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
        => (await TryReadHandoverProblemAsync(response, ct))?.Reason;

    private static async Task<HandoverProblemBody?> TryReadHandoverProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<HandoverProblemBody>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record HandoverIssueRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("code_hash")] string? CodeHash);

    private sealed record HandoverVerifyRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("success")] bool Success);

    // S06 heartbeat body: { "lat": <lat>, "lng": <lng> } (delivery-service
    // heartbeatRequest shape; both keys lowercase). Explicit names lock the wire
    // format independent of the global naming policy.
    private sealed record HeartbeatRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("lat")] double Lat,
        [property: System.Text.Json.Serialization.JsonPropertyName("lng")] double Lng);

    private sealed record HandoverProblemBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string? Reason,
        [property: System.Text.Json.Serialization.JsonPropertyName("attempts_remaining")] int? AttemptsRemaining,
        [property: System.Text.Json.Serialization.JsonPropertyName("escalation_id")] string? EscalationId,
        // delivery-service stamps locked_at (RFC3339) on the 423 body; the
        // controller echoes it rather than synthesizing a gateway clock value.
        [property: System.Text.Json.Serialization.JsonPropertyName("locked_at")] DateTimeOffset? LockedAt);
}
