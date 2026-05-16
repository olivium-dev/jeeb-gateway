using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Kyc;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IAuthServiceClient"/>.
/// Hand-coded against the published routes on auth-service main (see
/// JeebKycController.cs, JeebAdminKycController.cs, JeebAuthController.cs)
/// pending an NSwag spec we can generate from. The named HttpClient
/// ("auth") supplies BaseAddress + the org-standard resilience pipeline
/// from <c>ServiceClientExtensions</c>; this class never has to think
/// about retry/timeout/circuit-breaker.
/// </summary>
public sealed class AuthServiceClient : IAuthServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AuthServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<KycSubmissionResponse> SubmitKycAsync(KycSubmitUpstreamRequest request, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(request.VehicleType), "vehicleType" },
            { new StringContent(request.VehicleRegistration), "vehicleRegistration" }
        };

        AddFile(content, "idFront", request.IdFront);
        AddFile(content, "idBack", request.IdBack);
        AddFile(content, "selfie", request.Selfie);

        using var message = new HttpRequestMessage(HttpMethod.Post, "api/jeeb/kyc/submit")
        {
            Content = content
        };
        // X-User-Id propagates the authenticated caller from the gateway's
        // JWT to the upstream service, which trusts gateway-injected headers
        // for the BFF aggregation pattern.
        message.Headers.Add("X-User-Id", request.UserId);

        using var response = await _http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<KycSubmissionResponse>(response, ct);
    }

    public async Task<KycStatusResponse> GetKycStatusAsync(string userId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "api/jeeb/kyc/status");
        message.Headers.Add("X-User-Id", userId);

        using var response = await _http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<KycStatusResponse>(response, ct);
    }

    public async Task<KycQueueResponse> AdminKycQueueAsync(int page, int pageSize, CancellationToken ct)
    {
        var url = $"api/jeeb/admin/kyc/queue?page={page}&pageSize={pageSize}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<KycQueueResponse>(response, ct);
    }

    public async Task<KycReviewResponse> AdminKycReviewAsync(string submissionId, string actingUserId, KycReviewRequest body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, $"api/jeeb/admin/kyc/{submissionId}/review")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        message.Headers.Add("X-User-Id", actingUserId);

        using var response = await _http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<KycReviewResponse>(response, ct);
    }

    public async Task<TokenRefreshUpstreamResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/jeeb/auth/refresh",
            new { refreshToken },
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<TokenRefreshUpstreamResponse>(response, ct);
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/jeeb/auth/logout",
            new { refreshToken },
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
    }

    private static void AddFile(MultipartFormDataContent multipart, string name, KycFile file)
    {
        var byteContent = new ByteArrayContent(file.Bytes);
        byteContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
        multipart.Add(byteContent, name, file.FileName);
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
