using System;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner;

/// <summary>
/// Durable store for the PP-7 partner top-up OTP step-up challenge (partner-wallet-bff). A
/// partner→jeeber top-up above <see cref="PartnerWalletOptions.OtpStepUpThreshold"/> must mint a
/// challenge here and then present the code back on the confirm before the wallet-service saga runs.
///
/// <para><b>Money-authorization contract.</b> The code is stored HASHED (SHA-256), never raw and
/// never logged. A challenge is bound to <c>(partnerId, jeeberId, amount)</c> — a confirm that does
/// not match all three is refused (<see cref="PartnerOtpOutcome.Mismatch"/>), so a partner cannot
/// reuse a code minted for a different jeeber/amount and cannot use another partner's code.
/// <list type="bullet">
///   <item><b>Single-use.</b> <see cref="ValidateAndConsumeAsync"/> claims a unique consumption key
///   through jeeb-state-service's atomic insert-or-return primitive, so a concurrent double-submit
///   or a later replay authorizes AT MOST ONE money move
///   (<see cref="PartnerOtpOutcome.Consumed"/>) — the same remote atomic guarantee the sibling
///   <see cref="IPartnerWalletOperationStore"/> uses for its own no-double-spend invariant.</item>
///   <item><b>Bounded guesses.</b> Each validation atomically reserves one of the finite attempt keys
///   (<see cref="PartnerOtpOutcome.WrongCode"/>); once the
///   <see cref="PartnerOtpChallengePolicy.MaxAttempts"/> ceiling is reached the challenge hard-expires
///   (<see cref="PartnerOtpOutcome.Exhausted"/>).</item>
///   <item><b>Time-bounded.</b> A challenge past <c>expires_at</c> is
///   <see cref="PartnerOtpOutcome.Expired"/>.</item>
/// </list></para>
///
/// <para>The implementation is a stateless gateway adapter over jeeb-state-service. Challenge
/// hashes, attempt reservations, and consumption markers live in the owning service; the gateway
/// opens no DB and keeps no in-process challenge store.</para>
/// </summary>
public interface IPartnerOtpChallengeStore
{
    /// <summary>
    /// Persist a new challenge for <paramref name="partnerId"/> to top up <paramref name="jeeberId"/>
    /// by <paramref name="amount"/>, holding the SHA-256 <paramref name="codeHash"/> (hex) and
    /// <paramref name="expiresAt"/>. Returns the new challenge id.
    /// </summary>
    Task<Guid> IssueAsync(
        Guid partnerId, Guid jeeberId, double amount, string codeHash, DateTimeOffset expiresAt,
        CancellationToken ct);

    /// <summary>
    /// Validate a confirm's <paramref name="challengeId"/> against the presented
    /// <paramref name="codeHash"/> (SHA-256 hex, compared CONSTANT-TIME) and the bound
    /// <c>(partnerId, jeeberId, amount)</c>, and — only on a full match — CONSUME it atomically
    /// (single-use). See <see cref="PartnerOtpOutcome"/> for every verdict. A wrong code carries the
    /// remaining attempts in <see cref="PartnerOtpValidation.AttemptsRemaining"/>.
    /// </summary>
    Task<PartnerOtpValidation> ValidateAndConsumeAsync(
        Guid challengeId, Guid partnerId, Guid jeeberId, double amount, string codeHash,
        CancellationToken ct);
}

/// <summary>The verdict of a challenge validation. Only <see cref="Valid"/> authorizes a money move.</summary>
public enum PartnerOtpOutcome
{
    /// <summary>Matched, unexpired, unused, correct code — consumed atomically; the transfer may proceed.</summary>
    Valid = 0,

    /// <summary>No challenge with that id (unknown / already swept). Maps to 403 otp-invalid.</summary>
    NotFound = 1,

    /// <summary>The challenge exists but its bound partner/jeeber/amount does not match. 403 otp-invalid.</summary>
    Mismatch = 2,

    /// <summary>Past its validity window (expires_at). 403 otp-invalid.</summary>
    Expired = 3,

    /// <summary>Wrong code; the attempt was counted. 403 otp-invalid (+ attemptsRemaining).</summary>
    WrongCode = 4,

    /// <summary>The wrong-guess ceiling was reached — hard-expired. 403 otp-exhausted.</summary>
    Exhausted = 5,

    /// <summary>Already consumed by a prior successful confirm (replay). 403 otp-consumed.</summary>
    Consumed = 6,
}

/// <summary>A validation verdict plus, for a <see cref="PartnerOtpOutcome.WrongCode"/>, the attempts left.</summary>
public sealed record PartnerOtpValidation(PartnerOtpOutcome Outcome, int AttemptsRemaining);

/// <summary>
/// Frozen PP-7 policy constants shared by both store impls and the challenge service, so the
/// state-service adapter and challenge service enforce the exact same ceiling / window / code shape.
/// </summary>
public static class PartnerOtpChallengePolicy
{
    /// <summary>Max wrong-code guesses before the challenge hard-expires (otp-exhausted).</summary>
    public const int MaxAttempts = 5;

    /// <summary>Challenge validity window in seconds (the frozen 5-minute window).</summary>
    public const int TtlSeconds = 300;

    /// <summary>Number of decimal digits in the one-time code.</summary>
    public const int CodeDigits = 6;
}
