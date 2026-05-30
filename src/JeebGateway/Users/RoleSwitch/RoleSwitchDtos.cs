using System.Text.Json.Serialization;

namespace JeebGateway.Users.RoleSwitch;

/// <summary>
/// T-BE-003 / JEB-39 — request body for <c>POST /v1/users/me/role/switch</c>.
/// Per the Jira AC1 example the field is simply <c>role</c>.
/// </summary>
public sealed class RoleSwitchRequest
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

/// <summary>
/// T-BE-003 / JEB-39 — response body for <c>POST /v1/users/me/role/switch</c>.
/// Returns the fresh JWT pair so the mobile app can swap tokens in one round
/// trip (no separate <c>/v1/auth/refresh</c> hop), plus a user snapshot so
/// the local profile state stays in sync without an extra GET.
/// </summary>
public sealed class RoleSwitchResponse
{
    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("tokenType")]
    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("accessTokenExpiresAt")]
    public required DateTimeOffset AccessTokenExpiresAt { get; init; }

    [JsonPropertyName("refreshTokenExpiresAt")]
    public required DateTimeOffset RefreshTokenExpiresAt { get; init; }

    [JsonPropertyName("user")]
    public required RoleSwitchUserBlock User { get; init; }
}

/// <summary>
/// Subset of the user profile included in <see cref="RoleSwitchResponse.User"/>.
/// Field shape matches user-management's <c>UserRolesResponse</c> (T-BE-002)
/// to keep gateway↔service contracts consistent.
/// </summary>
public sealed class RoleSwitchUserBlock
{
    [JsonPropertyName("userId")]
    public required Guid UserId { get; init; }

    [JsonPropertyName("available_roles")]
    public required IReadOnlyList<string> AvailableRoles { get; init; }

    [JsonPropertyName("active_role")]
    public required string ActiveRole { get; init; }

    [JsonPropertyName("active_role_changed_at")]
    public DateTimeOffset? ActiveRoleChangedAt { get; init; }
}

/// <summary>
/// Canonical <c>type</c> URIs used in the ProblemDetails bodies emitted by
/// <see cref="JeebGateway.Controllers.V1.UsersRoleController"/>. Pinned as
/// constants so a typo in a controller never silently desyncs from a QA
/// scenario file or a mobile error-handling switch.
///
/// AC1 success → no ProblemDetails.
/// AC2 (Sami requests jeeber but only has [client]) →
///   403 + <see cref="RoleNotAvailable"/>.
/// AC3 (request 'admin' or any unknown role) →
///   400 + <see cref="InvalidRole"/>.
/// </summary>
public static class RoleSwitchProblemTypes
{
    public const string InvalidRole       = "https://jeeb.dev/errors/invalid_role";
    public const string RoleNotAvailable  = "https://jeeb.dev/errors/role_not_available";
    public const string Unauthenticated   = "https://jeeb.dev/errors/unauthenticated";
    public const string UserNotFound      = "https://jeeb.dev/errors/user_not_found";
}

/// <summary>
/// Short-name shape used as the <c>title</c> for ProblemDetails. Stable
/// across versions; mobile maps these to localised copy.
/// </summary>
public static class RoleSwitchProblemTitles
{
    public const string InvalidRole       = "invalid_role";
    public const string RoleNotAvailable  = "role_not_available";
    public const string Unauthenticated   = "unauthenticated";
    public const string UserNotFound      = "user_not_found";
}
