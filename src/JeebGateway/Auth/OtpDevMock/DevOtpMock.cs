using System.Collections.Concurrent;
using JeebGateway.Tokens;
using JeebGateway.Users;

namespace JeebGateway.Auth.OtpDevMock;

/// <summary>
/// Outcome codes for a mock OTP verify, mapped 1:1 to the FROZEN RFC 7807
/// problem-type set the restored <c>/v1/auth/otp/*</c> contract emits
/// (<c>invalid_otp</c> → 401, <c>too_many_attempts</c> → 429).
/// </summary>
public enum DevOtpVerifyOutcome
{
    /// <summary>Correct fixed code on a live, non-locked OTP → a real session minted.</summary>
    Ok,

    /// <summary>Wrong / expired / no-record code → 401 <c>invalid_otp</c>.</summary>
    InvalidOtp,

    /// <summary>Attempt cap reached → 429 <c>too_many_attempts</c>; locked until a new request.</summary>
    TooManyAttempts,
}

/// <summary>Result of <see cref="IDevOtpMock.RequestAsync"/>.</summary>
public sealed record DevOtpRequestResult(int TtlSeconds);

/// <summary>Result of <see cref="IDevOtpMock.VerifyAsync"/> — carries a REAL minted session on success.</summary>
public sealed record DevOtpVerifyResult(
    DevOtpVerifyOutcome Outcome,
    string? AccessToken = null,
    string? RefreshToken = null,
    string? UserId = null,
    string? ActiveRole = null,
    string[]? AvailableRoles = null);

/// <summary>
/// Credential-free, in-process OTP mock used ONLY behind the
/// <c>Features:DevEndpoints:Enabled</c> flag (the same gate as
/// <see cref="JeebGateway.Security.DevOnlyAttribute"/>). It performs NO upstream
/// call, sends NO SMS, and never touches the shared <c>one-time-password</c>
/// service or any Twilio credential.
///
/// <para><b>Anti-gaming.</b> A verify succeeds ONLY for the single fixed test
/// code (<see cref="FixedTestCode"/> = the harness <c>OTP_LOGIN_CODE</c>). Any
/// other code FAILS (<see cref="DevOtpVerifyOutcome.InvalidOtp"/>), increments a
/// per-phone attempt counter, and once the counter reaches
/// <see cref="AttemptCap"/> the OTP is LOCKED
/// (<see cref="DevOtpVerifyOutcome.TooManyAttempts"/>) until a fresh
/// <see cref="RequestAsync"/> clears it — even a subsequently-correct code keeps
/// failing while locked. This makes a mock that auto-passes impossible.</para>
/// </summary>
public interface IDevOtpMock
{
    /// <summary>
    /// Begin a mock OTP for <paramref name="phone"/>: (re)issues a fresh,
    /// deterministic-TTL OTP record and RESETS the attempt counter / lock. No
    /// SMS, no upstream. Returns the deterministic <c>ttlSeconds</c>.
    /// </summary>
    DevOtpRequestResult RequestAsync(string phone);

    /// <summary>
    /// Verify <paramref name="code"/> for <paramref name="phone"/>. Mints a REAL
    /// gateway session (find-or-create user + <see cref="ITokenService"/>) ONLY on
    /// the correct fixed code against a live, non-locked OTP; otherwise fails and
    /// applies the attempt cap.
    /// </summary>
    Task<DevOtpVerifyResult> VerifyAsync(string phone, string code, CancellationToken ct);
}

/// <inheritdoc cref="IDevOtpMock"/>
public sealed class DevOtpMock : IDevOtpMock
{
    /// <summary>The ONE code that passes — the harness <c>OTP_LOGIN_CODE</c>.</summary>
    public const string FixedTestCode = "123456";

    /// <summary>Wrong-code attempts allowed before the OTP locks (matches S02 SM-OTP cap = 3).</summary>
    public const int AttemptCap = 3;

    /// <summary>Deterministic TTL surfaced to the client (S02 locks ttl ≈ 300 s).</summary>
    public const int TtlSeconds = 300;

    private readonly IUsersStore _users;
    private readonly ITokenService _tokens;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, OtpRecord> _records = new(StringComparer.Ordinal);

    public DevOtpMock(IUsersStore users, ITokenService tokens, TimeProvider clock)
    {
        _users = users;
        _tokens = tokens;
        _clock = clock;
    }

    public DevOtpRequestResult RequestAsync(string phone)
    {
        var key = Normalize(phone);
        var now = _clock.GetUtcNow();
        // A fresh request supersedes any prior record AND clears the lock/attempts
        // (count-based lockout clears ONLY by requesting a new OTP — S02 N2b).
        _records[key] = new OtpRecord(now.AddSeconds(TtlSeconds));
        return new DevOtpRequestResult(TtlSeconds);
    }

    public async Task<DevOtpVerifyResult> VerifyAsync(string phone, string code, CancellationToken ct)
    {
        var key = Normalize(phone);
        var now = _clock.GetUtcNow();

        // No record (or expired) → treat as an invalid OTP. Never auto-create.
        if (!_records.TryGetValue(key, out var record) || record.ExpiresAt <= now)
        {
            return new DevOtpVerifyResult(DevOtpVerifyOutcome.InvalidOtp);
        }

        lock (record.Gate)
        {
            // Already locked by a prior cap breach → stays locked until a new
            // request, regardless of the code presented (anti-gaming).
            if (record.Locked)
            {
                return new DevOtpVerifyResult(DevOtpVerifyOutcome.TooManyAttempts);
            }

            // Wrong code → count the attempt; lock once the cap is reached.
            if (!FixedTimeEquals(code, FixedTestCode))
            {
                record.Attempts++;
                if (record.Attempts >= AttemptCap)
                {
                    record.Locked = true;
                    return new DevOtpVerifyResult(DevOtpVerifyOutcome.TooManyAttempts);
                }
                return new DevOtpVerifyResult(DevOtpVerifyOutcome.InvalidOtp);
            }

            // Correct code: consume the OTP so it cannot be replayed.
            _records.TryRemove(key, out _);
        }

        // Mint a REAL session exactly like the production verify path: find-or-create
        // the user (keyed on the normalized phone, matching the historical sign-in
        // path), then issue an access+refresh pair via the existing TokenService.
        var profile = await _users.GetOrCreateAsync(key, ct);
        var pair = await _tokens.IssueAsync(profile.Id, profile.Roles, ct);

        return new DevOtpVerifyResult(
            DevOtpVerifyOutcome.Ok,
            AccessToken: pair.AccessToken,
            RefreshToken: pair.RefreshToken,
            UserId: profile.Id,
            ActiveRole: string.IsNullOrWhiteSpace(profile.ActiveRole) ? Roles.Client : profile.ActiveRole,
            AvailableRoles: profile.Roles.ToArray());
    }

    private static string Normalize(string phone) => (phone ?? string.Empty).Trim();

    /// <summary>
    /// Constant-time string compare so the fixed-code check has no timing
    /// side-channel (even for a mock — keeps the parity with real verifiers).
    /// </summary>
    private static bool FixedTimeEquals(string? provided, string expected)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(provided ?? string.Empty);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        // FixedTimeEquals requires equal lengths; a length mismatch is simply not
        // equal (the fixed test code length is public, so no secret leaks here).
        return a.Length == b.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed class OtpRecord
    {
        public OtpRecord(DateTimeOffset expiresAt) => ExpiresAt = expiresAt;
        public DateTimeOffset ExpiresAt { get; }
        public int Attempts { get; set; }
        public bool Locked { get; set; }
        public object Gate { get; } = new();
    }
}
