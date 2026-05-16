using System.Collections.Concurrent;

namespace JeebGateway.Tokens;

/// <summary>
/// MVP in-memory refresh-token / revocation list. The collection
/// doubles as the revocation list: a token is "revoked" iff its
/// <see cref="RefreshToken.RevokedAt"/> is set, and lookups go
/// through <see cref="FindByHashAsync"/> on every refresh.
///
/// Production swap (see follow-up migration 0007 + the Redis line in
/// docker-compose.yml):
///   - <c>_byId</c> moves to the <c>refresh_tokens</c> Postgres table
///     keyed on token_id (UUID), with token_hash UNIQUE for the lookup
///     path.
///   - The revocation list itself becomes a Redis SET, populated by an
///     outbox trigger on the same DB transaction that writes the
///     revocation row. JWT bearer middleware then checks Redis on every
///     access-token use for the suspended-user fast path (suspension
///     should bite faster than the 15-min access-token TTL when the
///     access token is presented via Authorization header).
///   - Example wiring (commented; not enabled in the MVP build):
///       // services.AddSingleton&lt;IConnectionMultiplexer&gt;(sp =&gt;
///       //     ConnectionMultiplexer.Connect(opts.RedisConnectionString));
///       // services.AddSingleton&lt;IRefreshTokenStore, RedisRefreshTokenStore&gt;();
/// </summary>
public class InMemoryRefreshTokenStore : IRefreshTokenStore
{
    private readonly ConcurrentDictionary<string, RefreshToken> _byId = new();
    private readonly ConcurrentDictionary<string, string> _hashToId = new();
    private readonly object _writeLock = new();

    public Task AddAsync(RefreshToken token, CancellationToken ct)
    {
        lock (_writeLock)
        {
            _byId[token.TokenId] = token;
            _hashToId[token.TokenHash] = token.TokenId;
        }
        return Task.CompletedTask;
    }

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct)
    {
        if (_hashToId.TryGetValue(tokenHash, out var id) && _byId.TryGetValue(id, out var token))
        {
            return Task.FromResult<RefreshToken?>(token);
        }
        return Task.FromResult<RefreshToken?>(null);
    }

    public Task<bool> RotateAsync(string oldTokenId, RefreshToken replacement, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(oldTokenId, out var old)) return Task.FromResult(false);
            if (!old.IsActive(DateTimeOffset.UtcNow)) return Task.FromResult(false);

            old.RevokedAt = DateTimeOffset.UtcNow;
            old.RevokedReason = RevocationReason.Rotated.ToString();
            old.ReplacedByTokenId = replacement.TokenId;

            _byId[replacement.TokenId] = replacement;
            _hashToId[replacement.TokenHash] = replacement.TokenId;
            return Task.FromResult(true);
        }
    }

    public Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (_byId.TryGetValue(tokenId, out var t) && t.RevokedAt is null)
            {
                t.RevokedAt = DateTimeOffset.UtcNow;
                t.RevokedReason = reason.ToString();
            }
        }
        return Task.CompletedTask;
    }

    public Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        lock (_writeLock)
        {
            foreach (var t in _byId.Values.Where(t => t.UserId == userId && t.RevokedAt is null))
            {
                t.RevokedAt = now;
                t.RevokedReason = reason.ToString();
                count++;
            }
        }
        return Task.FromResult(count);
    }

    public Task<int> RevokeChainAsync(string startTokenId, RevocationReason reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(startTokenId, out var start)) return Task.FromResult(0);

            // Walk forward through ReplacedByTokenId, and revoke every token
            // belonging to the same user (covers detached siblings).
            foreach (var t in _byId.Values.Where(t => t.UserId == start.UserId && t.RevokedAt is null))
            {
                t.RevokedAt = now;
                t.RevokedReason = reason.ToString();
                count++;
            }
        }
        return Task.FromResult(count);
    }
}
