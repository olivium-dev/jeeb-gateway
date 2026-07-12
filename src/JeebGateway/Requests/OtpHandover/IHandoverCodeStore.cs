using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// Gap G4 (run-24 CHECK C) — the in-app delivery handover code.
///
/// The customer must be able to SEE the delivery handover code IN-APP. The only
/// moment a code can ride the surface the cycle-4 mobile client reads is the
/// OFFER-ACCEPT response (<c>POST /v1/offers/{offerId}/accept</c>), so the gateway
/// mints the code at accept, returns it ONCE — owner-scoped — as
/// <c>handoverCode</c>, and later matches it at handover so the code the customer
/// saw actually verifies (verify-precedence: additive to the existing SMS /
/// one-time-password flow, NEVER replacing it — a miss falls through to the SMS
/// code which keeps working unchanged).
///
/// The code lives in the cross-replica-safe <see cref="IDistributedCache"/> (Redis
/// in prod, <c>AddDistributedMemoryCache</c> in MVP/tests) keyed by
/// <c>deliveryId</c> (== requestId), so an accept on one replica and a handover
/// verify on another still agree. It is NEVER logged and is only ever returned to
/// the delivery's OWN client — every call site is owner-scoped. Storing the raw
/// code at rest is consistent with the codebase's existing precedent
/// (<see cref="DeliveryRequest.DeliveryOtp"/> is likewise a raw handover OTP at rest).
///
/// NON-BREAKING: a brand-new focused interface — no existing store or contract
/// changes, and the real implementation works verbatim in the integration host
/// (the in-memory distributed cache is already registered there).
/// </summary>
public interface IHandoverCodeStore
{
    /// <summary>
    /// Mint-or-return the delivery's 4-digit handover code and persist it
    /// (owner-retrievable) under a delivery-lifecycle TTL. Idempotent per delivery:
    /// a repeat call (idempotent re-accept, or the <c>GET /otp</c> store-miss
    /// fallback) returns the SAME code, so the customer's in-app code and the code
    /// the jeeber enters can never diverge. The raw code is never logged.
    /// </summary>
    Task<string> IssueAsync(string deliveryId, CancellationToken ct);

    /// <summary>
    /// Owner-scoped re-read of the delivery's current handover code, or null when
    /// none is stored (never issued / TTL expired). Backs the
    /// <c>GET /v1/deliveries/{id}/otp</c> store-miss fallback the mobile client
    /// uses when its local HandoverCodeStore is empty. Callers MUST have already
    /// verified the caller owns the delivery — this store performs no auth itself.
    /// </summary>
    Task<string?> GetAsync(string deliveryId, CancellationToken ct);

    /// <summary>
    /// Verify-precedence probe: true when a handover code is stored for the delivery
    /// AND it matches <paramref name="submittedCode"/> (constant-time compare, no
    /// timing side-channel). False on no-stored-code OR mismatch — the caller then
    /// falls through to the existing one-time-password validation so the SMS-minted
    /// code keeps working unchanged. The submitted code is never logged.
    /// </summary>
    Task<bool> TryMatchAsync(string deliveryId, string submittedCode, CancellationToken ct);

    /// <summary>
    /// JEBV4-83 (F7) — invalidate the delivery's stored handover code after a
    /// SUCCESSFUL handover so the Gap-G4 in-app secret does not live out its full
    /// 24h TTL as a stale, still-matchable code. DEGRADE-DON'T-FAIL: the handover
    /// already verified and transitioned upstream, so a cache-infrastructure fault
    /// here must never fail the response — the code then self-heals via its own TTL.
    /// Idempotent and no-op when nothing is stored (never issued / already cleared).
    /// The code is never logged.
    /// </summary>
    Task InvalidateAsync(string deliveryId, CancellationToken ct);
}

/// <summary>
/// <see cref="IDistributedCache"/>-backed <see cref="IHandoverCodeStore"/>.
/// Cross-replica-safe (Redis in prod, <c>AddDistributedMemoryCache</c> in MVP /
/// tests) so an accept that lands on one replica and a handover verify that lands
/// on another still agree on the code.
/// </summary>
public sealed class DistributedCacheHandoverCodeStore : IHandoverCodeStore
{
    // Covers a full delivery lifecycle (accept -> pickup -> drop-off -> handover),
    // which can span hours — deliberately far longer than the 15-min OTP
    // attempt/lockout window. Short enough that a never-completed delivery's code
    // self-heals out of the cache within a day.
    private static readonly TimeSpan CodeTtl = TimeSpan.FromHours(24);

    private static string CacheKey(string deliveryId) => $"otp:handovercode:{deliveryId}";

    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedCacheHandoverCodeStore>? _log;

    public DistributedCacheHandoverCodeStore(IDistributedCache cache)
        : this(cache, null)
    {
    }

    public DistributedCacheHandoverCodeStore(IDistributedCache cache, ILogger<DistributedCacheHandoverCodeStore>? log)
    {
        _cache = cache;
        _log = log;
    }

    public async Task<string> IssueAsync(string deliveryId, CancellationToken ct)
    {
        // Idempotent: reuse the code already issued for this delivery so a re-accept
        // or a fallback read never mints a second, divergent code.
        var existing = await GetAsync(deliveryId, ct);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        // Cryptographically-random 4-digit code — RandomNumberGenerator, never
        // Random, so the code is not predictable (a fixed dev seed is brute-forceable).
        var code = RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4", CultureInfo.InvariantCulture);
        await _cache.SetAsync(
            CacheKey(deliveryId),
            Encoding.UTF8.GetBytes(code),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CodeTtl },
            ct);
        return code;
    }

    public async Task<string?> GetAsync(string deliveryId, CancellationToken ct)
    {
        var stored = await _cache.GetAsync(CacheKey(deliveryId), ct);
        return stored is { Length: > 0 } ? Encoding.UTF8.GetString(stored) : null;
    }

    public async Task<bool> TryMatchAsync(string deliveryId, string submittedCode, CancellationToken ct)
    {
        byte[]? stored;
        try
        {
            stored = await _cache.GetAsync(CacheKey(deliveryId), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCacheInfrastructureFault(ex))
        {
            // JEBV4-38 (PP-3) — degrade-don't-fail, mirroring
            // RedisOtpRequestRateLimiter's fail-open precedent. A Redis blip on
            // this read must never 500 the money-adjacent handover-verify path
            // (it fires COD settlement). Treat the fault as a MISS — the caller
            // (DeliveriesController.VerifyHandoverOtp) falls through to the
            // existing SMS one-time-password validation on any miss, so this is
            // NOT a bypass: a wrong code is still independently rejected by that
            // SMS check. Only the in-app-minted-code short-circuit is
            // unavailable while Redis is down.
            _log?.LogWarning(ex,
                "handover_code.cache_fault deliveryId={DeliveryId} op=try_match; failing open to SMS verify",
                deliveryId);
            return false;
        }

        if (stored is null || stored.Length == 0)
        {
            return false;
        }

        var submitted = Encoding.UTF8.GetBytes(submittedCode ?? string.Empty);
        // FixedTimeEquals is constant-time for equal-length inputs and safely
        // returns false (no throw, no data-dependent early-out) on a length mismatch.
        return CryptographicOperations.FixedTimeEquals(stored, submitted);
    }

    public async Task InvalidateAsync(string deliveryId, CancellationToken ct)
    {
        // JEBV4-83 (F7) — degrade-don't-fail, mirroring TryMatchAsync's fail-open
        // precedent. A Redis blip on this post-verify cleanup must never fail the
        // already-committed, already-verified handover response; the stale code then
        // self-heals via its own 24h TTL instead of an explicit clear.
        try
        {
            await _cache.RemoveAsync(CacheKey(deliveryId), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCacheInfrastructureFault(ex))
        {
            _log?.LogWarning(ex,
                "handover_code.cache_fault deliveryId={DeliveryId} op=invalidate; code will self-heal via TTL",
                deliveryId);
        }
    }

    /// <summary>
    /// JEBV4-38 (PP-3) — recognises a cache-INFRASTRUCTURE fault (Redis
    /// unreachable/timeout) so it can be told apart from a genuine
    /// application error. <see cref="RedisException"/> covers
    /// <c>RedisConnectionException</c> / <c>RedisTimeoutException</c> /
    /// <c>RedisServerException</c> — exactly what
    /// <c>RedisOtpRequestRateLimiter.TryAcquire</c> catches for its own
    /// fail-open precedent. <see cref="TimeoutException"/> is included
    /// defensively for the (rare) case a client-side timeout surfaces as the
    /// BCL type rather than StackExchange.Redis's own subtype. The in-memory
    /// <see cref="IDistributedCache"/> used in dev/tests never throws either,
    /// so this catch is a safe no-op outside of a real Redis deployment.
    /// </summary>
    private static bool IsCacheInfrastructureFault(Exception ex) =>
        ex is RedisException or TimeoutException;
}
