using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Per-phone AND per-IP sliding-window burst guard for the sign-in OTP REQUEST
/// leg (S02 F-E, JEB-37 — EXC-4 <c>rate_limited</c>). This is distinct from the
/// per-OTP attempt cap (which the shared one-time-password service owns,
/// count-based, S02 N2) and from the generic ASP.NET
/// <c>RateLimitingExtensions</c> global limiter: this one is a dedicated,
/// gateway-local guard that trips a <b>429 rate_limited</b> BEFORE the upstream
/// <c>SendOTP</c> is dialed, so a throttled request never costs an SMS.
///
/// <para><b>Both windows are AND-checked:</b> a request is throttled when EITHER
/// the per-IP window OR the per-phone window is exhausted (S02 design:
/// "&gt;10/min/IP AND &gt;3/min/phone"). Each leg uses its own sliding 60 s
/// window.</para>
///
/// <para><b>PII governance (S02 N13):</b> the raw E.164 is NEVER used as a key —
/// it is SHA-256 hashed first, so neither the in-memory dictionary nor any
/// future Redis key holds a raw phone.</para>
///
/// <para><b>Replica caveat (M3):</b> the default implementation is a per-process
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. With N gateway replicas the
/// effective caps scale with replica count; production should bind a
/// Redis/state-service-backed implementation (the durable-store choice flagged to
/// the owner). The interface is the seam; the rotation/limit semantics do not
/// change when the store is swapped.</para>
/// </summary>
public interface IOtpRequestRateLimiter
{
    /// <summary>
    /// Records a request hit for the (<paramref name="ipKey"/>,
    /// <paramref name="rawPhone"/>) pair and returns whether the caller is now
    /// throttled. When this returns true the caller MUST NOT dial the upstream.
    /// </summary>
    bool TryAcquire(string? ipKey, string? rawPhone);
}

/// <summary>Options for <see cref="InMemoryOtpRequestRateLimiter"/> (bound from
/// <c>Auth:Otp:RateLimit</c>). Caps are configuration so an environment can tune
/// the burst guard without a code change; defaults match the S02 design.</summary>
public sealed class OtpRequestRateLimitOptions
{
    public const string SectionName = "Auth:Otp:RateLimit";

    /// <summary>When false the guard admits every request (no throttle). Lets a
    /// load test or a non-prod env disable the burst guard. Defaults true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Max OTP requests per sliding window per source IP. Default 10.</summary>
    public int MaxPerIpPerWindow { get; set; } = 10;

    /// <summary>Max OTP requests per sliding window per phone. Default 3.</summary>
    public int MaxPerPhonePerWindow { get; set; } = 3;

    /// <summary>Sliding window length in seconds. Default 60.</summary>
    public int WindowSeconds { get; set; } = 60;
}

/// <inheritdoc />
public sealed class InMemoryOtpRequestRateLimiter : IOtpRequestRateLimiter
{
    private readonly OtpRequestRateLimitOptions _opts;
    private readonly TimeProvider _clock;

    // bucketKey -> timestamps of hits within the window. Pruned on each touch.
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public InMemoryOtpRequestRateLimiter(
        Microsoft.Extensions.Options.IOptions<OtpRequestRateLimitOptions> options,
        TimeProvider clock)
    {
        _opts = options.Value;
        _clock = clock;
    }

    public bool TryAcquire(string? ipKey, string? rawPhone)
    {
        if (!_opts.Enabled) return true;

        var now = _clock.GetUtcNow();
        var window = TimeSpan.FromSeconds(Math.Max(1, _opts.WindowSeconds));

        // Per-IP and per-phone are checked together; a throttle on EITHER throttles
        // the request. Record both legs so each independently advances its window.
        var ipOk = Hit($"ip:{Normalize(ipKey)}", now, window, _opts.MaxPerIpPerWindow);
        var phoneOk = Hit($"ph:{HashPhone(rawPhone)}", now, window, _opts.MaxPerPhonePerWindow);

        return ipOk && phoneOk;
    }

    /// <summary>
    /// Records a hit in <paramref name="key"/>'s sliding window and returns true
    /// when the count (including this hit) is within <paramref name="max"/>.
    /// </summary>
    private bool Hit(string key, DateTimeOffset now, TimeSpan window, int max)
    {
        var w = _windows.GetOrAdd(key, _ => new Window());
        lock (w.Gate)
        {
            var cutoff = now - window;
            w.Hits.RemoveAll(t => t < cutoff);
            w.Hits.Add(now);
            return w.Hits.Count <= max;
        }
    }

    private static string Normalize(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();

    private static string HashPhone(string? rawPhone)
    {
        var key = (rawPhone ?? string.Empty).Trim();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Convert.ToHexString(hash);
    }

    private sealed class Window
    {
        public readonly object Gate = new();
        public readonly List<DateTimeOffset> Hits = new();
    }
}
