using System.Text.Json.Serialization;

namespace JeebGateway.Auth.OtpSignIn;

// ---------------------------------------------------------------------------
// S02 Wave-1 (ADR-003) snake_case contract for the dual-role BFF surfaces
//   GET  /v1/users/me                 (F-B)
//   POST /v1/users/me/role/switch     (F-A)
//
// Bodies are emitted in the FROZEN Jeeb client contract vocabulary
// ({client,jeeber}); the gateway's JeebRoleTranslator is the only place that
// vocabulary is bound to user-management's opaque {customer,driver}.
// ---------------------------------------------------------------------------

/// <summary>Response for <c>GET /v1/users/me</c> — snake_case dual-role identity.</summary>
public sealed class UsersMeResponse
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("active_role")]
    public string ActiveRole { get; init; } = string.Empty;

    [JsonPropertyName("available_roles")]
    public string[] AvailableRoles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; init; }
}

/// <summary>Request body for <c>POST /v1/users/me/role/switch</c> — Jeeb contract role.</summary>
public sealed class RoleSwitchRequestDto
{
    /// <summary>The target Jeeb contract role: <c>client</c> or <c>jeeber</c>.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

/// <summary>
/// Response for <c>POST /v1/users/me/role/switch</c>. <see cref="AccessToken"/> /
/// <see cref="RefreshToken"/> are the UM-RE-ISSUED tokens, returned VERBATIM — the
/// gateway signs nothing on this path (CP-C / N11). <see cref="ActiveRole"/> and
/// <see cref="AvailableRoles"/> are translated back to the snake_case contract.
/// </summary>
public sealed class RoleSwitchResponseDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("active_role")]
    public string ActiveRole { get; init; } = string.Empty;

    [JsonPropertyName("available_roles")]
    public string[] AvailableRoles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;
}
