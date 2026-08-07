namespace JeebGateway.Tokens;

/// <summary>
/// A persisted refresh token record. The opaque token value itself is
/// NEVER stored — only its SHA-256 hash — so a leak of the table cannot
/// be replayed against the gateway.
///
/// Rotation forms a singly-linked chain via <see cref="ReplacedByTokenId"/>:
/// presenting a token that is already <see cref="RevokedAt"/> and has a
/// <see cref="ReplacedByTokenId"/> is treated as reuse / theft and
/// revokes the entire chain for that user.
/// </summary>
public class RefreshToken
{
    public required string TokenId { get; init; }
    public required string UserId { get; init; }

    /// <summary>SHA-256 hash (base64url) of the raw token value.</summary>
    public required string TokenHash { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Verified upstream ceremony context, if one existed at original login.
    /// Rotation copies these values unchanged; it never advances authentication
    /// freshness. Older records omit them and therefore fail MFA checks closed.
    /// </summary>
    public long? AuthenticationTime { get; init; }
    public IReadOnlyList<string>? AuthenticationMethods { get; init; }

    /// <summary>
    /// External operator sessions carry their verified provider, bounded session
    /// deadline, display projection, and the exact mapped gateway roles. Ordinary
    /// mobile/user-management sessions leave these fields null and continue to
    /// re-resolve roles through their existing path.
    /// </summary>
    public string? IdentityProvider { get; init; }
    public DateTimeOffset? AuthenticationSessionExpiresAt { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<string>? RoleSnapshot { get; init; }
    public string? ActiveRoleSnapshot { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? ReplacedByTokenId { get; set; }

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && ExpiresAt > now;
}

public enum RevocationReason
{
    Rotated,
    Logout,
    PasswordChanged,
    PhoneChanged,
    Suspended,
    ReuseDetected,
    AccountDeleted,
    Reauthenticated,
}
