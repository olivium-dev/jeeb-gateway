namespace JeebGateway.Tokens;

public interface ITokenService
{
    Task<TokenPair> IssueAsync(string userId, IEnumerable<string> roles, CancellationToken ct);

    Task<TokenPair> IssueAsync(
        string userId,
        IEnumerable<string> roles,
        string activeRole,
        VerifiedAuthenticationContext? authentication,
        CancellationToken ct) => IssueAsync(userId, roles, ct);

    /// <summary>
    /// Rotate a refresh token: validate, revoke the presented one, and
    /// return a fresh access + refresh pair. Reuse of an already-rotated
    /// token revokes the entire chain and returns <see cref="RefreshOutcome.ReuseDetected"/>.
    /// </summary>
    Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct);

    Task<RefreshResult> RefreshAsync(
        string refreshToken,
        Func<string, CancellationToken, Task<TokenRoleContext?>> roleResolver,
        CancellationToken ct) => RefreshAsync(refreshToken, ct);

    Task RevokeAsync(string refreshToken, RevocationReason reason, CancellationToken ct);

    Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct);
}

public sealed record TokenRoleContext(
    IReadOnlyList<string> Roles,
    string ActiveRole,
    VerifiedAuthenticationContext? Authentication = null);

/// <summary>
/// Authentication ceremony claims that have already passed issuer, audience,
/// lifetime, and signature verification. The internal constructor prevents
/// controllers from fabricating an MFA ceremony.
/// </summary>
public sealed class VerifiedAuthenticationContext
{
    internal VerifiedAuthenticationContext(
        long authTime,
        IReadOnlyList<string> methods,
        string? provider = null,
        DateTimeOffset? sessionExpiresAt = null,
        string? displayName = null,
        string? email = null,
        bool persistRoleContext = false)
    {
        AuthTime = authTime;
        Methods = methods;
        Provider = provider;
        SessionExpiresAt = sessionExpiresAt;
        DisplayName = displayName;
        Email = email;
        PersistRoleContext = persistRoleContext;
    }

    public long AuthTime { get; }
    public IReadOnlyList<string> Methods { get; }
    public string? Provider { get; }
    public DateTimeOffset? SessionExpiresAt { get; }
    public string? DisplayName { get; }
    public string? Email { get; }
    public bool PersistRoleContext { get; }
}

public class TokenPair
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset AccessTokenExpiresAt { get; init; }
    public required DateTimeOffset RefreshTokenExpiresAt { get; init; }
}

public enum RefreshOutcome
{
    Ok,
    NotFound,
    Expired,
    Revoked,
    ReuseDetected,
    RoleResolutionFailed,
    AuthenticationExpired,
}

public class RefreshResult
{
    public required RefreshOutcome Outcome { get; init; }
    public TokenPair? Tokens { get; init; }
}
