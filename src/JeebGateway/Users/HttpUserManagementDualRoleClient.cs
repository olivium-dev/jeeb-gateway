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

        // JEB-1480 (GR2): the shared user-management phone-identity endpoint is now
        // IDENTITY-ONLY ({ userId, isNew, phone }) and performs NO role/claim shaping.
        // The DEFAULT role ("customer") is decorated HERE; the user's FULL persisted
        // role set (available_roles + active_role) is read separately via
        // GetUserRolesAsync (GET /api/User/{userId}/roles) so an OTP login reflects an
        // already-granted driver role (JEEBER-SPINE Defect 1). A freshly resolved
        // identity with no roles row defaults to the gateway's single 'customer' role.
        return new PhoneFindOrCreateResult(
            dto.UserId ?? string.Empty,
            dto.IsNew,
            new[] { Roles.Client },
            Roles.Client);
    }

    public async Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
    {
        // JEEBER-SPINE Defect 1 — read the user's PERSISTED role set so a gateway-minted
        // OTP session reflects an already-granted driver role (and the DB-set active_role)
        // WITHOUT inventing it. UM is the authority; the gateway only reads + projects.
        // A 404 (no roles row yet) or any non-success is non-fatal: the caller keeps the
        // safe default ('customer'), so a UM blip never hard-breaks an OTP login.
        using var resp = await _http.GetAsync($"api/User/{Uri.EscapeDataString(userId)}/roles", ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "user-management get-roles returned {Status} for userId={UserId}",
                (int)resp.StatusCode, userId);
            return null;
        }

        var dto = await resp.Content.ReadFromJsonAsync<UserRolesBodyResponse>(Json, ct);
        if (dto is null)
        {
            return null;
        }

        var roles = dto.AvailableRoles is { Length: > 0 } ? dto.AvailableRoles : Array.Empty<string>();
        var active = string.IsNullOrWhiteSpace(dto.ActiveRole) ? null : dto.ActiveRole;
        return new UserRolesResult(dto.UserId ?? userId, roles, active);
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

    // JEB-1480 (GR2): mirrors the de-leaked shared contract — IDENTITY ONLY. The
    // shared service no longer emits available_roles/active_role on this surface.
    private sealed class PhoneFindOrCreateBodyResponse
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("isNew")] public bool IsNew { get; set; }
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
        // UM's RoleSwitchReissueResponse emits snake_case active_role (verified against the
        // live :10001 swagger). The prior camelCase mapping never bound, silently falling
        // back to the requested role (JEEBER-SPINE Defect 2 hardening).
        [JsonPropertyName("active_role")] public string? ActiveRole { get; set; }
    }

    // UM's UserRolesResponse (GET /api/User/{userId}/roles, PATCH active-role):
    // { userId, available_roles[], active_role } — snake_case role fields.
    private sealed class UserRolesBodyResponse
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("available_roles")] public string[]? AvailableRoles { get; set; }
        [JsonPropertyName("active_role")] public string? ActiveRole { get; set; }
    }
}
