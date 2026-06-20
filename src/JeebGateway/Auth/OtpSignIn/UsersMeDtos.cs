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
/// Response for <c>POST /v1/users/me/role/switch</c>. <see cref="ActiveRole"/> and
/// <see cref="AvailableRoles"/> are translated back to the snake_case contract.
///
/// <para>DEFECT-1 (iter5): <see cref="AccessToken"/> / <see cref="RefreshToken"/> are
/// emitted EMPTY. UM re-issues a token pair on this path, but that pair carries
/// iss/aud=user-management which 401s on the gateway's aud=jeeb-clients /v1/* routes —
/// relaying it broke the live session. ADR-004 ("single session token carries the full
/// role set"): the switch persists the active_role and the caller KEEPS its existing
/// valid aud=jeeb-clients session, so no replacement token is handed back. The fields are
/// retained (empty) for response-shape stability with existing consumers.</para>
/// </summary>
public sealed class RoleSwitchResponseDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    // Flat fields retained for backward compatibility with any existing consumer.
    [JsonPropertyName("active_role")]
    public string ActiveRole { get; init; } = string.Empty;

    [JsonPropertyName("available_roles")]
    public string[] AvailableRoles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;

    // S02 contract (ADR-003): the role/switch body carries a nested user block so the
    // dual-role identity is addressable at $.user.active_role / $.user.available_roles —
    // the same shape as the verify response (OtpVerifyResponse.User). Harness H-B4 / ALT-3
    // assert $.user.active_role. Additive: the flat fields above are still emitted.
    [JsonPropertyName("user")]
    public RoleSwitchUserBlock User { get; init; } = new();
}

/// <summary>The nested user block inside <see cref="RoleSwitchResponseDto"/>.</summary>
public sealed class RoleSwitchUserBlock
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("active_role")]
    public string ActiveRole { get; init; } = string.Empty;

    [JsonPropertyName("available_roles")]
    public string[] AvailableRoles { get; init; } = Array.Empty<string>();
}
