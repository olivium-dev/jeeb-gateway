using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Redis-backed <see cref="IOtpRequestRateLimiter"/> — the durable, cross-replica
/// implementation of the sign-in OTP-request burst guard (S02 F-E, JEB-37 —
/// EXC-4 <c>rate_limited</c>; PR #32 review B2 / AC-GatewayRateLimit).
///
/// <para><b>Why.</b> <see cref="InMemoryOtpRequestRateLimiter"/> is a per-process
/// <c>ConcurrentDictionary</c>: with N gateway replicas the per-phone cap becomes
/// 3 × N/min and the per-IP cap 10 × N/min — both bypassable (the M3 replica caveat
/// called out on the interface). This implementation moves the two sliding windows
/// into Redis so the caps hold ACROSS replicas and survive a gateway bounce. It is
/// bound in production only, gated on <c>GatewayRateLimit:RedisConnectionString</c>;
/// where that key is absent (local dev / CI / Testing) the in-memory fallback is kept
/// unchanged, so this is an additive, non-breaking swap.</para>
///
/// <para><b>Semantics are byte-for-byte the InMemory ones.</b> Both windows are
/// AND-checked (a request is throttled when EITHER the per-IP OR the per-phone window
/// is exhausted), both legs are always recorded so each independently advances its own
/// 60 s sliding window, the disabled switch admits every request, and the caps come
/// from the SAME <see cref="OtpRequestRateLimitOptions"/> (<c>Auth:Otp:RateLimit</c>:
/// per-phone 3/min, per-IP 10/min, 60 s window). Time is read from the injected
/// <see cref="TimeProvider"/> exactly as the in-memory limiter reads it.</para>
///
/// <para><b>PII governance (S02 N13).</b> The raw E.164 is NEVER a Redis key — it is
/// SHA-256 hashed first (same <c>HashPhone</c> as the in-memory limiter), so no Redis
/// key holds a raw phone.</para>
///
/// <para><b>Sliding window = one Redis sorted set per key.</b> Each key's window is a
/// ZSET whose members are individual hits scored by their epoch-millisecond timestamp.
/// One touch runs, ATOMICALLY (a single server-side Lua <c>EVAL</c>, mirroring the
/// in-memory per-key <c>lock</c>):
/// <list type="number">
///   <item><c>ZADD</c> score=now member=unique — record this hit.</item>
///   <item><c>ZREMRANGEBYSCORE key -inf (cutoff</c> — evict hits with score &lt; now-window
///         (exclusive upper bound ⇒ the strict <c>t &lt; cutoff</c> the in-memory prune uses).</item>
///   <item><c>ZCARD</c> — the surviving hit count (this hit included).</item>
///   <item><c>PEXPIRE key window</c> — bound idle-key growth (the in-memory dict prunes on touch).</item>
/// </list>
/// The count &lt;= max comparison is done here, exactly as the in-memory <c>Hit</c> does.</para>
///
/// <para><b>Degrade-don't-fail.</b> A Redis blip must never turn the sign-in path into a
/// 500 (the in-memory limiter cannot fail). On any <see cref="RedisException"/> the guard
/// FAILS OPEN — it admits the request, exactly as a disabled guard would — because the
/// burst cap is a best-effort "a throttle must never cost an SMS" guard, never an auth gate.</para>
/// </summary>
public sealed class RedisOtpRequestRateLimiter : IOtpRequestRateLimiter
{
    // Distinct keyspaces for the two legs (the removed chat-topology map / refresh-token
    // fallback may share the same Redis, so the limiter namespaces its keys).
    private const string IpKeyPrefix = "otp-rl:ip:";
    private const string PhoneKeyPrefix = "otp-rl:ph:";

    /// <summary>
    /// Atomic sliding-window touch (KEYS[1]=window key; ARGV[1]=now score, ARGV[2]=unique
    /// member, ARGV[3]=cutoff, ARGV[4]=ttl ms) returning the post-prune hit count. Runs the
    /// exact ZADD / ZREMRANGEBYSCORE / ZCARD / PEXPIRE quartet server-side so the whole touch
    /// is atomic — the Redis equivalent of the in-memory per-key lock. The exclusive
    /// <c>'(' .. ARGV[3]</c> upper bound reproduces the in-memory prune's strict <c>t &lt; cutoff</c>.
    /// </summary>
    private const string SlidingWindowLua =
        "redis.call('ZADD', KEYS[1], ARGV[1], ARGV[2])\n" +
        "redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', '(' .. ARGV[3])\n" +
        "local count = redis.call('ZCARD', KEYS[1])\n" +
        "redis.call('PEXPIRE', KEYS[1], ARGV[4])\n" +
        "return count";

    private readonly IConnectionMultiplexer _redis;
    private readonly OtpRequestRateLimitOptions _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<RedisOtpRequestRateLimiter> _log;

    public RedisOtpRequestRateLimiter(
        IConnectionMultiplexer redis,
        IOptions<OtpRequestRateLimitOptions> options,
        TimeProvider clock,
        ILogger<RedisOtpRequestRateLimiter> log)
    {
        _redis = redis;
        _opts = options.Value;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public bool TryAcquire(string? ipKey, string? rawPhone)
    {
        if (!_opts.Enabled) return true;

        var now = _clock.GetUtcNow();
        var window = TimeSpan.FromSeconds(Math.Max(1, _opts.WindowSeconds));

        try
        {
            var db = _redis.GetDatabase();

            // Per-IP and per-phone are checked together; a throttle on EITHER throttles
            // the request. Record both legs so each independently advances its window —
            // identical to InMemoryOtpRequestRateLimiter.TryAcquire.
            var ipOk = Hit(db, IpKeyPrefix + Normalize(ipKey), now, window, _opts.MaxPerIpPerWindow);
            var phoneOk = Hit(db, PhoneKeyPrefix + HashPhone(rawPhone), now, window, _opts.MaxPerPhonePerWindow);

            return ipOk && phoneOk;
        }
        catch (RedisException ex)
        {
            // Degrade-don't-fail: a Redis outage must not 500 the sign-in path. Admit
            // (fail-open) exactly as a disabled guard would — the burst cap is a
            // best-effort SMS-cost guard, not an authentication gate.
            _log.LogWarning(ex, "OTP-request rate limiter: Redis unavailable; admitting request (fail-open).");
            return true;
        }
    }

    /// <summary>
    /// Records a hit in <paramref name="key"/>'s sliding window and returns true when the
    /// count (including this hit) is within <paramref name="max"/> — the Redis-sorted-set
    /// analogue of <see cref="InMemoryOtpRequestRateLimiter"/>'s <c>Hit</c>.
    /// </summary>
    private bool Hit(IDatabase db, string key, DateTimeOffset now, TimeSpan window, int max)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var windowMs = (long)window.TotalMilliseconds;
        var cutoffMs = nowMs - windowMs;
        // Unique member per hit — the score carries the timestamp, so two hits in the
        // same millisecond must still be distinct ZSET members (else ZADD would overwrite
        // the earlier one and undercount).
        var member = $"{nowMs}-{Guid.NewGuid():N}";

        var result = db.ScriptEvaluate(
            SlidingWindowLua,
            new RedisKey[] { key },
            new RedisValue[] { nowMs, member, cutoffMs, windowMs });

        var count = (long)result;
        return count <= max;
    }

    /// <summary>Mirrors <see cref="InMemoryOtpRequestRateLimiter"/>'s IP normalisation.
    /// Internal (not private) purely so the swap's PII/normalisation contract is unit-testable
    /// without a live Redis (InternalsVisibleTo → JeebGateway.IntegrationTests).</summary>
    internal static string Normalize(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();

    /// <summary>SHA-256 phone hash (S02 N13) — identical to
    /// <see cref="InMemoryOtpRequestRateLimiter"/>'s <c>HashPhone</c> so the raw E.164 is
    /// never a Redis key. Internal for the same test-visibility reason as <see cref="Normalize"/>.</summary>
    internal static string HashPhone(string? rawPhone)
    {
        var key = (rawPhone ?? string.Empty).Trim();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Convert.ToHexString(hash);
    }
}
