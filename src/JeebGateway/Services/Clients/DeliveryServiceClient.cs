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
        using var message = new HttpRequestMessage(HttpMethod.Post, "jeeb/jeebers/me/availability")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.Add("X-User-Id", jeeberId);

        using var response = await _http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<JeeberAvailabilityUpstream>(response, ct);
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
}
