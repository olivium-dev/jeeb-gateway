using System;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner;

/// <summary>
/// Crypto + orchestration seam for the PP-7 partner top-up OTP step-up. Owns the code lifecycle
/// (cryptographically-random 6-digit generation, SHA-256 hashing, the 5-minute TTL) and delegates
/// persistence to <see cref="IPartnerOtpChallengeStore"/>. Keeps the controller thin and the raw code
/// out of every other layer — the code is returned to the controller ONCE from
/// <see cref="IssueAsync"/> (surfaced in-app as devCode only under the dev-endpoints flag) and is
/// never logged.
/// </summary>
public interface IPartnerOtpChallengeService
{
    /// <summary>
    /// Mint a step-up challenge for a partner→jeeber top-up: generate a random code, store its
    /// SHA-256 hash bound to <c>(partnerId, jeeberId, amount)</c> with a TTL, and return the challenge
    /// id, the validity window, and the RAW code (for the controller to surface in-app as devCode only
    /// when the dev-endpoints flag is on — never otherwise).
    /// </summary>
    Task<PartnerOtpIssued> IssueAsync(Guid partnerId, Guid jeeberId, double amount, CancellationToken ct);

    /// <summary>
    /// Validate a confirm's code against the challenge and, on a full match, consume it single-use.
    /// The raw <paramref name="code"/> is hashed here and compared constant-time in the store.
    /// </summary>
    Task<PartnerOtpValidation> VerifyAsync(
        Guid challengeId, Guid partnerId, Guid jeeberId, double amount, string code, CancellationToken ct);
}

/// <summary>A freshly minted challenge: its id, validity window, and the RAW code (dev-surfaced only).</summary>
public sealed record PartnerOtpIssued(Guid ChallengeId, int ExpiresInSeconds, string RawCode);
