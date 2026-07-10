using System.Text.Json;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.Tokens;

/// <summary>
/// Durable <see cref="IRefreshTokenStore"/> backed by <b>jeeb-state-service</b>'s
/// opaque idempotency KV (the same general key→opaque-body store with GET-by-key +
/// prefix-scan that <see cref="JeebGateway.Disputes.StateServiceDisputeStore"/> and
/// <see cref="JeebGateway.JeebSupport.StateServiceSupportTicketStore"/> reuse).
/// Replaces <see cref="InMemoryRefreshTokenStore"/>, whose token rows AND revocation
/// list evaporated on every gateway bounce / replica move — so reuse-detection could
/// neither survive a restart nor span replicas.
///
/// <para><b>The missing READ + revoke path.</b> <see cref="StateServiceRefreshFamilyWriter"/>
/// already mirrors create/rotate server-side (family reuse-detection + revocation are
/// enforced in the state-service). This store is the complementary durable READ +
/// revoke half the gateway's <see cref="TokenService"/> needs: find-by-hash,
/// single-use rotation, logout/suspension revocation, and family burn on reuse.</para>
///
/// <para><b>Mutable state on an insert-once KV.</b> The KV is exactly-once
/// (<c>INSERT … ON CONFLICT (key) DO NOTHING</c>) — a key cannot be overwritten. A
/// token's revocation state is mutable (active → revoked/rotated), so state changes
/// are modelled as an APPEND-ONLY chain of version keys
/// <c>refresh-token-status:{tokenId}:{seq}</c>, each carrying the full row snapshot at
/// that revision. The immutable base row lives at <c>refresh-token:{tokenId}</c> (seq
/// 0). A read resolves the row by taking the highest existing seq — the same
/// monotone-write pattern <see cref="JeebGateway.Disputes.StateServiceDisputeStore"/>
/// uses. Insert-once on the next seq key doubles as the single-winner rotation guard:
/// the caller that fails to insert the revocation lost the race and treats the
/// presented token as reuse.</para>
///
/// <para><b>Indexes.</b> Two secondary-index families are written alongside the base
/// row so the non-by-id reads need no full-table scan:
/// <list type="bullet">
///   <item><c>refresh-token-hash:{tokenHash}</c> → tokenId (point GET for
///     <see cref="FindByHashAsync"/>).</item>
///   <item><c>refresh-token-user:{userId}:{tokenId}</c> → tokenId (prefix scan for
///     <see cref="RevokeAllForUserAsync"/> / <see cref="RevokeChainAsync"/>).</item>
/// </list>
/// Index rows are written ONCE at create time (immutable hash↔id / user↔id edges); the
/// live revocation state is always re-read from the status chain so a stale index value
/// never masks a revocation.</para>
///
/// <para><b>TTL.</b> 90 days — comfortably exceeds the 30-day refresh-token lifetime
/// (<see cref="JwtOptions.RefreshTokenDays"/>) so a rotated/revoked token's tombstone
/// always outlives any still-presentable token (reuse-detection correctness) while
/// bounding KV growth.</para>
/// </summary>
public sealed class StateServiceRefreshTokenStore : IRefreshTokenStore
{
    internal const string RowKeyPrefix = "refresh-token:";
    internal const string StatusKeyPrefix = "refresh-token-status:";
    internal const string HashKeyPrefix = "refresh-token-hash:";
    internal const string UserKeyPrefix = "refresh-token-user:";

    /// <summary>90-day TTL (seconds) — outlives the 30-day refresh lifetime.</summary>
    internal const int TtlSeconds = 90 * 24 * 60 * 60;

    /// <summary>Defensive upper bound on the per-token status chain length.</summary>
    private const int MaxStatusRevisions = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _kv;
    private readonly ILogger<StateServiceRefreshTokenStore> _logger;

    public StateServiceRefreshTokenStore(IIdempotencyStore kv, ILogger<StateServiceRefreshTokenStore> logger)
    {
        _kv = kv;
        _logger = logger;
    }

    public async Task AddAsync(RefreshToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);

        // Base (seq 0) immutable snapshot — the by-id anchor.
        await _kv.PutOrGetAsync(RowKeyPrefix + token.TokenId, statusCode: 201, Serialize(token), TtlSeconds, ct);

        // Hash + user indexes (immutable edges) so find-by-hash is a point GET and
        // revoke-all/revoke-chain are prefix scans, not full scans. The live
        // revocation state is always re-resolved from the status chain.
        await _kv.PutOrGetAsync(HashKeyPrefix + token.TokenHash, 201, token.TokenId, TtlSeconds, ct);
        await _kv.PutOrGetAsync(
            UserKeyPrefix + token.UserId + ":" + token.TokenId, 201, token.TokenId, TtlSeconds, ct);
    }

    public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokenHash)) return null;

        // Point GET the hash index → tokenId, then resolve the latest snapshot.
        var indexOutcome = await _kv.GetAsync(HashKeyPrefix + tokenHash, ct);
        var tokenId = UnwrapId(indexOutcome?.ResponseBodyJson);
        if (tokenId is null) return null;

        return await ResolveLatestAsync(tokenId, ct);
    }

    public async Task<bool> RotateAsync(string oldTokenId, RefreshToken replacement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (string.IsNullOrWhiteSpace(oldTokenId)) return false;

        var (latest, seq) = await ResolveLatestWithSeqAsync(oldTokenId.Trim(), ct);
        if (latest is null) return false;

        var now = DateTimeOffset.UtcNow;
        // Only an active token may be rotated (matches the in-memory contract):
        // already-revoked / expired / missing ⇒ false, which the caller treats as reuse.
        if (!latest.IsActive(now)) return false;

        // Prepare the presented token's rotation revision (not persisted yet).
        latest.RevokedAt = now;
        latest.RevokedReason = RevocationReason.Rotated.ToString();
        latest.ReplacedByTokenId = replacement.TokenId;

        // PP-11 (JEBV4-39) — FAIL-OPEN ordering. Durably persist the NEW replacement
        // token BEFORE revoking/rotating the OLD one. If the process crashes or the
        // state-service write fails between the two steps, the OLD token stays active and
        // the whole refresh is safely retryable: the caller re-presents the still-valid
        // old token and rotates again. The previous revoke-old-then-persist-new ordering
        // was fail-CLOSED — a failure after the revoke but before the replacement
        // persisted stranded the user with neither a valid old nor a delivered new token,
        // forcing a re-login.
        //
        // AddAsync is NOT atomic: it issues THREE independent, non-atomic PutOrGetAsync
        // writes (base row, hash index, user index). A crash BETWEEN those three can leave
        // a PARTIALLY-written replacement — that is still safe here, because the
        // replacement's raw value is never returned unless RotateAsync returns true, so a
        // partial row is an unreachable orphan and a retry re-runs all three insert-once
        // writes idempotently.
        //
        // Trade-offs of fail-open (accepted per PP-11):
        //   • A brief window where BOTH the old and the new token are valid (the old is
        //     revoked microseconds later, on the very next write). The replacement's raw
        //     value is NOT returned to the caller until this method returns true, so it is
        //     undeliverable during that window — the only externally-usable token then is
        //     the old one the caller already held.
        //   • On a lost concurrency race (the seq+1 guard below) the just-persisted
        //     replacement becomes an unreachable, TTL-bounded orphan (its raw value is
        //     never handed to any caller). But note a SHARED hazard this reorder makes
        //     DETERMINISTIC: on a benign concurrent double-refresh of the SAME old token,
        //     the loser's RotateAsync returns false and TokenService treats it as reuse,
        //     calling RevokeChainAsync — whose UNCONDITIONAL revoke-all-for-user burns
        //     EVERY active token for the user, INCLUDING the winner's just-delivered live
        //     token. The winner is thus silently logged out on its next refresh. This
        //     collateral burn is PRE-EXISTING (present under the old ordering too), but was
        //     racy there because the winner's replacement was persisted only AFTER the
        //     guard; persisting it BEFORE the guard here means the burn always sees it.
        //     Distinguishing benign collision from true replay needs a wider RotateAsync
        //     return contract — deferred to the owner, out of PP-11 scope.
        await AddAsync(replacement, ct);

        // Insert-once on the next seq remains the authoritative single-winner rotation
        // guard: if a concurrent refresh already took seq+1 we lost the race → treat as
        // reuse and abandon our (never-delivered) replacement. RotateAsync returns true
        // for exactly one winner, so the reuse-detection contract at this boundary is
        // unchanged by the reorder above.
        var nextSeq = seq + 1;
        var outcome = await _kv.PutOrGetAsync(
            StatusKeyPrefix + latest.TokenId + ":" + nextSeq, statusCode: 200, Serialize(latest), TtlSeconds, ct);
        if (!outcome.Inserted)
        {
            _logger.LogWarning(
                "refresh-token {TokenId} concurrent rotate at seq {Seq}; treating as reuse", latest.TokenId, nextSeq);
            return false;
        }

        return true;
    }

    public async Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokenId)) return;

        var (latest, seq) = await ResolveLatestWithSeqAsync(tokenId.Trim(), ct);
        // No-op if missing or already revoked (idempotent logout).
        if (latest is null || latest.RevokedAt is not null) return;

        latest.RevokedAt = DateTimeOffset.UtcNow;
        latest.RevokedReason = reason.ToString();

        var nextSeq = seq + 1;
        var outcome = await _kv.PutOrGetAsync(
            StatusKeyPrefix + latest.TokenId + ":" + nextSeq, statusCode: 200, Serialize(latest), TtlSeconds, ct);
        if (!outcome.Inserted)
        {
            // Lost the race; the token was concurrently revoked/rotated — already
            // terminal, so a plain revoke is a no-op.
            _logger.LogInformation(
                "refresh-token {TokenId} concurrent revoke at seq {Seq}; no-op", latest.TokenId, nextSeq);
        }
    }

    public Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Task.FromResult(0);
        return RevokeActiveForUserAsync(userId.Trim(), reason, ct);
    }

    public async Task<int> RevokeChainAsync(string startTokenId, RevocationReason reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(startTokenId)) return 0;

        // Resolve the chain's owner, then revoke every active token for that user
        // (covers detached siblings) — the durable mirror of the in-memory walk.
        var start = await ResolveLatestAsync(startTokenId.Trim(), ct);
        if (start is null) return 0;

        return await RevokeActiveForUserAsync(start.UserId, reason, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<int> RevokeActiveForUserAsync(string userId, RevocationReason reason, CancellationToken ct)
    {
        var ids = await ListIdsByPrefixAsync(UserKeyPrefix + userId + ":", ct);
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var id in ids)
        {
            var (latest, seq) = await ResolveLatestWithSeqAsync(id, ct);
            if (latest is null || latest.RevokedAt is not null) continue;

            latest.RevokedAt = now;
            latest.RevokedReason = reason.ToString();

            var outcome = await _kv.PutOrGetAsync(
                StatusKeyPrefix + latest.TokenId + ":" + (seq + 1), statusCode: 200, Serialize(latest), TtlSeconds, ct);
            // Only count the tokens this call actually flipped active → revoked; a
            // lost seq race means a concurrent writer already revoked it.
            if (outcome.Inserted) count++;
        }
        return count;
    }

    private Task<RefreshToken?> ResolveLatestAsync(string tokenId, CancellationToken ct)
        => ResolveLatestWithSeqAsync(tokenId, ct).ContinueWith(t => t.Result.Row, ct,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    private async Task<(RefreshToken? Row, int Seq)> ResolveLatestWithSeqAsync(string tokenId, CancellationToken ct)
    {
        // Base snapshot (seq 0).
        var baseOutcome = await _kv.GetAsync(RowKeyPrefix + tokenId, ct);
        var row = Deserialize(baseOutcome?.ResponseBodyJson);
        if (row is null) return (null, -1);

        // Walk the append-only status chain forward; the highest present seq wins.
        var seq = 0;
        for (var next = 1; next <= MaxStatusRevisions; next++)
        {
            var rev = await _kv.GetAsync(StatusKeyPrefix + tokenId + ":" + next, ct);
            var revRow = Deserialize(rev?.ResponseBodyJson);
            if (revRow is null) break;
            row = revRow;
            seq = next;
        }
        return (row, seq);
    }

    private async Task<IReadOnlyList<string>> ListIdsByPrefixAsync(string prefix, CancellationToken ct)
    {
        var outcomes = await _kv.FindByPrefixAsync(prefix, ct);
        // Index rows store the raw token id as their body.
        return outcomes
            .Select(o => UnwrapId(o.ResponseBodyJson))
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? UnwrapId(string? body)
    {
        var id = body?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static string Serialize(RefreshToken t) => JsonSerializer.Serialize(t, Json);

    private static RefreshToken? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<RefreshToken>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
