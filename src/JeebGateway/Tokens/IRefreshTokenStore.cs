namespace JeebGateway.Tokens;

/// <summary>
/// Persistence boundary for refresh tokens. The MVP implementation is
/// in-memory; production wiring will move to Postgres (see the planned
/// follow-up migration for the refresh_tokens table) without touching
/// callers.
/// </summary>
public interface IRefreshTokenStore
{
    Task AddAsync(RefreshToken token, CancellationToken ct);

    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>
    /// Atomically rotate <paramref name="oldTokenId"/> → <paramref name="replacement"/>:
    /// marks the old token revoked with reason <see cref="RevocationReason.Rotated"/>
    /// and persists the new one. Returns false if the old token is not in an
    /// active state (already revoked, expired, or missing) — callers should
    /// treat that as token reuse and call <see cref="RevokeChainAsync"/>.
    /// </summary>
    Task<bool> RotateAsync(string oldTokenId, RefreshToken replacement, CancellationToken ct);

    /// <summary>
    /// Revoke a single token (logout). No-op if already revoked.
    /// </summary>
    Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct);

    /// <summary>
    /// Revoke all active refresh tokens for a user. Used by suspension,
    /// password change, and phone-number change flows.
    /// </summary>
    Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct);

    /// <summary>
    /// Writes/reads a short-lived durable tombstone for one bounded Dev Tool refresh family. This
    /// closes cleanup/rotation races without revoking unrelated sessions owned by the same user.
    /// </summary>
    Task MarkBoundedSessionRevokedAsync(string sessionFamilyId, CancellationToken ct) =>
        throw new NotSupportedException(
            "This refresh-token store does not implement bounded-session revocation.");
    Task<bool> IsBoundedSessionRevokedAsync(string sessionFamilyId, CancellationToken ct) =>
        throw new NotSupportedException(
            "This refresh-token store does not implement bounded-session revocation.");

    /// <summary>Revoke only the refresh rows linked to one bounded Dev Tool family.</summary>
    Task<int> RevokeBoundedFamilyAsync(
        string sessionFamilyId,
        RevocationReason reason,
        CancellationToken ct) =>
        throw new NotSupportedException(
            "This refresh-token store does not implement bounded-family revocation.");

    /// <summary>
    /// Walk the rotation chain from <paramref name="startTokenId"/> and
    /// revoke every token that is or was active for the owning user.
    /// Invoked when reuse of a rotated token is detected.
    /// </summary>
    Task<int> RevokeChainAsync(string startTokenId, RevocationReason reason, CancellationToken ct);
}
