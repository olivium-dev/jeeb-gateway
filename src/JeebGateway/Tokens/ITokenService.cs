namespace JeebGateway.Tokens;

public interface ITokenService
{
    Task<TokenPair> IssueAsync(string userId, IEnumerable<string> roles, CancellationToken ct);

    /// <summary>
    /// Rotate a refresh token: validate, revoke the presented one, and
    /// return a fresh access + refresh pair. Reuse of an already-rotated
    /// token revokes the entire chain and returns <see cref="RefreshOutcome.ReuseDetected"/>.
    /// </summary>
    Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct);

    Task RevokeAsync(string refreshToken, RevocationReason reason, CancellationToken ct);

    Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct);
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
    ReuseDetected
}

public class RefreshResult
{
    public required RefreshOutcome Outcome { get; init; }
    public TokenPair? Tokens { get; init; }
}
