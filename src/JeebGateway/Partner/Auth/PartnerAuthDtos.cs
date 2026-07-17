using System;
using System.ComponentModel.DataAnnotations;

namespace JeebGateway.Partner.Auth;

/// <summary>
/// Partner login request. Field names match what the Jeeb Partner Portal already posts to
/// <c>POST /v1/partner/auth/login</c> (<c>{ identifier, password }</c>) — see the portal
/// <c>ApiClient.login</c> — so the portal binds without a client change (G1 contract-drift guard).
/// </summary>
public sealed class PartnerLoginRequest
{
    /// <summary>The partner login handle (email or partner id).</summary>
    [Required, MinLength(3), MaxLength(256)]
    public string Identifier { get; init; } = string.Empty;

    /// <summary>The partner secret. Verified against the provisioned SHA-256 hash; never stored or logged.</summary>
    [Required, MinLength(1), MaxLength(1024)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// Partner login response. The portal binds to <c>token</c> (the bearer it stores and sends as
/// <c>Authorization: Bearer</c>); the remaining fields expose the full gateway session (refresh +
/// expiry) and the partner profile basics for a richer client without breaking the minimal contract.
/// </summary>
public sealed class PartnerLoginResponse
{
    /// <summary>The access (bearer) token — the field the portal reads. Same value as <see cref="AccessToken"/>.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>Explicit alias of <see cref="Token"/> (access JWT), for clients that prefer the canonical name.</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>The rotating, single-use refresh token (opaque).</summary>
    public string RefreshToken { get; init; } = string.Empty;

    /// <summary>Always "Bearer".</summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>Seconds until the access token expires (relative), for client-side refresh scheduling.</summary>
    public int AccessTokenExpiresInSeconds { get; init; }

    /// <summary>Absolute access-token expiry (UTC).</summary>
    public DateTimeOffset AccessTokenExpiresAt { get; init; }

    /// <summary>Absolute refresh-token expiry (UTC).</summary>
    public DateTimeOffset RefreshTokenExpiresAt { get; init; }

    /// <summary>The signed-in partner's profile basics.</summary>
    public PartnerProfileDto Partner { get; init; } = new();
}

/// <summary>Non-secret partner profile basics returned on login.</summary>
public sealed class PartnerProfileDto
{
    /// <summary>The partner's wallet holder id (== user-management userId), also the token subject.</summary>
    public Guid PartnerId { get; init; }

    /// <summary>The verified login handle.</summary>
    public string Login { get; init; } = string.Empty;

    /// <summary>Human display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The partner role string (always "partner").</summary>
    public string Role { get; init; } = JeebGateway.Users.Roles.Partner;
}

/// <summary>
/// <b>[DevOnly]</b> request to provision a partner credential at runtime (test/dev only). Mirrors the
/// admin-provisioned config roster but accepts the plaintext secret (hashed by the store on the way in).
/// </summary>
public sealed class PartnerAuthDevSeedRequest
{
    [Required, MinLength(3), MaxLength(256)]
    public string Identifier { get; init; } = string.Empty;

    /// <summary>The partner's user-management userId (== wallet holder id). Must be a GUID.</summary>
    [Required]
    public string HolderId { get; init; } = string.Empty;

    [Required, MinLength(1), MaxLength(256)]
    public string DisplayName { get; init; } = string.Empty;

    [Required, MinLength(1), MaxLength(1024)]
    public string Password { get; init; } = string.Empty;
}
