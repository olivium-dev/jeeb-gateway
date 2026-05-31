using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Production <see cref="IUserManagementPhoneIdentityClient"/> adapter over the
/// sibling story <c>T-BE-001a</c> endpoint on <c>olivium-dev/user-management</c>:
///   <c>POST /api/users/phone-identity/find-or-create</c>
///   body  { "phone": "+961xxxxxxxx" }
///   200   { userId, isNew, phone, available_roles, active_role }
///   400   Problem(type=invalid_phone)
///
/// Hand-coded against the contract pinned in audit #14764 (the user-management
/// OpenAPI spec does not yet carry this path while T-BE-001a is in flight on
/// <c>feature/jeeb-v3-dual-role-identity</c>; this adapter targets the agreed
/// route and is swapped for the NSwag-generated client once the spec lands).
///
/// The typed <see cref="HttpClient"/> is registered in
/// <see cref="OtpSignInServiceCollectionExtensions.AddJeebOtpSignIn"/> with the
/// <c>UserManagementApi:BaseUrl</c> base address and the org-standard Polly
/// resilience pipeline. The response is read with a snake_case-tolerant
/// serializer so both <c>available_roles</c>/<c>availableRoles</c> bind.
/// </summary>
public sealed class UserManagementPhoneIdentityClient : IUserManagementPhoneIdentityClient
{
    // Web defaults are camelCase-insensitive on read; the explicit
    // JsonPropertyName attributes below pin the snake_case contract fields.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public UserManagementPhoneIdentityClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PhoneIdentityResponse> PhoneIdentityFindOrCreateAsync(
        string normalizedE164Phone, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/users/phone-identity/find-or-create",
            new FindOrCreateRequest(normalizedE164Phone),
            JsonOptions,
            ct);

        // EnsureSuccessStatusCode throws on 4xx/5xx; AuthOtpController.VerifyOtp
        // catches the resulting exception and fails closed with a 502 rather
        // than issuing a token against an unverified identity.
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<FindOrCreateResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                "user-management phone-identity/find-or-create returned an empty body.");
        }

        return new PhoneIdentityResponse(
            UserId: payload.UserId,
            IsNew: payload.IsNew,
            Phone: payload.Phone ?? normalizedE164Phone,
            AvailableRoles: payload.AvailableRoles ?? Array.Empty<string>(),
            ActiveRole: payload.ActiveRole ?? string.Empty);
    }

    private sealed record FindOrCreateRequest(
        [property: JsonPropertyName("phone")] string Phone);

    private sealed record FindOrCreateResponse(
        [property: JsonPropertyName("userId")] Guid UserId,
        [property: JsonPropertyName("isNew")] bool IsNew,
        [property: JsonPropertyName("phone")] string? Phone,
        [property: JsonPropertyName("available_roles")] string[]? AvailableRoles,
        [property: JsonPropertyName("active_role")] string? ActiveRole);
}
