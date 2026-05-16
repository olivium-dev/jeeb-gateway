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
    AccountDeleted
}
