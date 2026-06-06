using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Users;

/// <summary>
/// HTTP implementation of <see cref="IUserManagementDualRoleClient"/> over the named
/// <c>UserManagementDualRoleClient</c> <see cref="HttpClient"/> (same base address as the
/// existing <c>ServiceUserManagementClient</c>, registered in Program.cs with the bearer-
/// forwarding handler + Polly v8 resilience pipeline — N9). Speaks OPAQUE roles only.
/// </summary>
public sealed class HttpUserManagementDualRoleClient : IUserManagementDualRoleClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpUserManagementDualRoleClient> _log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpUserManagementDualRoleClient(
        HttpClient http,
        ILogger<HttpUserManagementDualRoleClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            "api/users/phone-identity/find-or-create",
            new PhoneFindOrCreateBody { Phone = phone },
            Json, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "user-management phone find-or-create returned {Status}",
                (int)resp.StatusCode);
            throw new UserManagementCallException("phone/find-or-create", (int)resp.StatusCode);
        }

        var dto = await resp.Content.ReadFromJsonAsync<PhoneFindOrCreateBodyResponse>(Json, ct)
            ?? throw new UserManagementCallException("phone/find-or-create", (int)HttpStatusCode.BadGateway);

        var roles = dto.AvailableRoles is { Length: > 0 }
            ? dto.AvailableRoles
            : new[] { Roles.Client };
        var active = string.IsNullOrWhiteSpace(dto.ActiveRole) ? Roles.Client : dto.ActiveRole!;

        return new PhoneFindOrCreateResult(dto.UserId ?? string.Empty, dto.IsNew, roles, active);
    }

    public async Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            "api/User/role/switch",
            new RoleSwitchBody { UserId = userId, Role = opaqueRole },
            Json, ct);

        // N5 / ALT-1 — UM's distinct role_not_available signal is a 403. Map straight through.
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new UserManagementRoleNotAvailableException(userId, opaqueRole);
        }

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "user-management role switch returned {Status} for userId={UserId}",
                (int)resp.StatusCode, userId);
            throw new UserManagementCallException("role/switch", (int)resp.StatusCode);
        }

        var dto = await resp.Content.ReadFromJsonAsync<RoleSwitchBodyResponse>(Json, ct)
            ?? throw new UserManagementCallException("role/switch", (int)HttpStatusCode.BadGateway);

        return new RoleSwitchReissueResult(
            dto.UserId ?? userId,
            dto.AccessToken ?? string.Empty,
            dto.RefreshToken ?? string.Empty,
            string.IsNullOrWhiteSpace(dto.ActiveRole) ? opaqueRole : dto.ActiveRole!);
    }

    public async Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
    {
        // ADR-0004 (H8): append to available_roles with set-semantics. UM owns the
        // jsonb mutation; the gateway only composes the call (kyc-service never does).
        using var resp = await _http.PostAsJsonAsync(
            "api/User/role/grant",
            new RoleGrantBody { UserId = userId, Role = opaqueRole },
            Json, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "user-management role grant returned {Status} for userId={UserId} role={Role}",
                (int)resp.StatusCode, userId, opaqueRole);
            throw new UserManagementCallException("role/grant", (int)resp.StatusCode);
        }

        var dto = await resp.Content.ReadFromJsonAsync<RoleGrantBodyResponse>(Json, ct)
            ?? throw new UserManagementCallException("role/grant", (int)HttpStatusCode.BadGateway);

        var roles = dto.AvailableRoles is { Length: > 0 }
            ? dto.AvailableRoles
            : new[] { opaqueRole };

        // Added: prefer the explicit upstream flag; otherwise infer (the role is now
        // present, so a missing flag defaults to "added" only when we cannot tell).
        var added = dto.Added ?? true;

        return new RoleGrantResult(dto.UserId ?? userId, roles, added);
    }

    // ---- wire DTOs (snake_case where UM emits snake_case; PascalCase request fields) ----

    private sealed class RoleGrantBody
    {
        [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    }

    private sealed class RoleGrantBodyResponse
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("available_roles")] public string[]? AvailableRoles { get; set; }
        [JsonPropertyName("added")] public bool? Added { get; set; }
    }


    private sealed class PhoneFindOrCreateBody
    {
        [JsonPropertyName("phone")] public string Phone { get; set; } = string.Empty;
    }

    private sealed class PhoneFindOrCreateBodyResponse
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("isNew")] public bool IsNew { get; set; }
        [JsonPropertyName("available_roles")] public string[]? AvailableRoles { get; set; }
        [JsonPropertyName("active_role")] public string? ActiveRole { get; set; }
    }

    private sealed class RoleSwitchBody
    {
        [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    }

    private sealed class RoleSwitchBodyResponse
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
        [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
        [JsonPropertyName("activeRole")] public string? ActiveRole { get; set; }
    }
}
