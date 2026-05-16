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
        using var response = await _http.GetAsync("jeeb/tiers", ct);
        response.EnsureSuccessStatusCode();
        // Upstream returns a raw JSON array — not wrapped in an envelope.
        return await DeserializeAsync<IReadOnlyList<DeliveryTierDto>>(response, ct);
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
