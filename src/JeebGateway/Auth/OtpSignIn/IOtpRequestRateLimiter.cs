using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// AC-GatewayRateLimit — sliding-minute rolling counter for the
/// <c>POST /v1/auth/otp/request</c> endpoint. Locked by audit #14764:
///   <list type="bullet">
///     <item>10 req / min / source-IP</item>
///     <item>3  req / min / normalized phone</item>
///   </list>
///
/// Implementation notes:
/// 1) The limiter sits ON THE CONTROLLER (not the global rate-limiter
///    middleware) so it can partition on the NORMALISED phone — which is
///    computed inside the controller AFTER body binding. Putting this in
///    the middleware would require duplicating phone normalisation in two
///    places.
/// 2) The MVP is a per-process ConcurrentDictionary of timestamp lists.
///    Production wiring swaps to Redis (ZADD ts; ZREMRANGEBYSCORE 0
///    (now-60s); ZCARD) keyed on phone-hash + IP. The interface is the
///    same — the controller call site does not change.
/// 3) Time is read from <see cref="TimeProvider"/> so tests with
///    <c>FakeTimeProvider</c> can slide the window deterministically.
/// </summary>
public interface IOtpRequestRateLimiter
{
    /// <summary>
    /// Attempt to consume one budget for the given <paramref name="normalizedPhone"/>
    /// and <paramref name="sourceIp"/>. Returns <see cref="RateLimitDecision.Allowed"/>
    /// if both per-phone and per-IP budgets are within the limit, otherwise
    /// <see cref="RateLimitDecision.Limited"/> with the recommended Retry-After.
    /// </summary>
    RateLimitDecision TryAcquire(string normalizedPhone, string sourceIp);
}

public readonly record struct RateLimitDecision(
    bool Allowed,
    TimeSpan RetryAfter,
    string? LimitedDimension);   // "phone" | "ip" | null when Allowed

public sealed class SlidingMinuteOtpRequestRateLimiter : IOtpRequestRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<GatewayRateLimitOptions> _options;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _phoneHits = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _ipHits = new();

    public SlidingMinuteOtpRequestRateLimiter(
        TimeProvider clock,
        IOptionsMonitor<GatewayRateLimitOptions> options)
    {
        _clock   = clock;
        _options = options;
    }

    public RateLimitDecision TryAcquire(string normalizedPhone, string sourceIp)
    {
        var now    = _clock.GetUtcNow();
        var cutoff = now - Window;
        var opts   = _options.CurrentValue;

        // Per-phone budget. Strict order matters: we evaluate per-phone FIRST
        // because it is the tighter cap (3 vs 10). On allow we then evaluate
        // per-IP and only commit if both pass.
        var phoneQ = _phoneHits.GetOrAdd(normalizedPhone, _ => new ConcurrentQueue<DateTimeOffset>());
        PruneExpired(phoneQ, cutoff);
        if (phoneQ.Count >= opts.PerPhonePerMin)
        {
            return new RateLimitDecision(
                Allowed:           false,
                RetryAfter:        ComputeRetryAfter(phoneQ, now),
                LimitedDimension:  "phone");
        }

        var ipQ = _ipHits.GetOrAdd(sourceIp, _ => new ConcurrentQueue<DateTimeOffset>());
        PruneExpired(ipQ, cutoff);
        if (ipQ.Count >= opts.PerIpPerMin)
        {
            return new RateLimitDecision(
                Allowed:           false,
                RetryAfter:        ComputeRetryAfter(ipQ, now),
                LimitedDimension:  "ip");
        }

        phoneQ.Enqueue(now);
        ipQ.Enqueue(now);
        return new RateLimitDecision(Allowed: true, RetryAfter: TimeSpan.Zero, LimitedDimension: null);
    }

    private static void PruneExpired(ConcurrentQueue<DateTimeOffset> queue, DateTimeOffset cutoff)
    {
        while (queue.TryPeek(out var head) && head < cutoff)
        {
            queue.TryDequeue(out _);
        }
    }

    private static TimeSpan ComputeRetryAfter(ConcurrentQueue<DateTimeOffset> queue, DateTimeOffset now)
    {
        if (queue.TryPeek(out var oldest))
        {
            var retry = (oldest + Window) - now;
            if (retry < TimeSpan.FromSeconds(1)) return TimeSpan.FromSeconds(1);
            return retry;
        }
        return TimeSpan.FromSeconds(1);
    }
}
