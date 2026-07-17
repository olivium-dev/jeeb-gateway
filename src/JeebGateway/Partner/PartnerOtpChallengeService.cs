using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner;

/// <summary>
/// Default <see cref="IPartnerOtpChallengeService"/>. Generates the one-time code with
/// <see cref="RandomNumberGenerator"/> (cryptographically random — NOT <see cref="System.Random"/>,
/// which is brute-forceable), stores only its SHA-256 hex hash via
/// <see cref="IPartnerOtpChallengeStore"/>, and never logs the raw code. Mirrors the gateway's
/// existing in-app handover-code precedent (DistributedCacheHandoverCodeStore) for random generation
/// and constant-time verification, but hashes at rest per the PP-7 contract.
/// </summary>
public sealed class PartnerOtpChallengeService : IPartnerOtpChallengeService
{
    // Exclusive upper bound for a CodeDigits-digit code (e.g. 6 digits → [0, 1_000_000)).
    private static readonly int CodeExclusiveMax = (int)Math.Pow(10, PartnerOtpChallengePolicy.CodeDigits);
    private static readonly string CodeFormat = "D" + PartnerOtpChallengePolicy.CodeDigits;

    private readonly IPartnerOtpChallengeStore _store;

    public PartnerOtpChallengeService(IPartnerOtpChallengeStore store)
    {
        _store = store;
    }

    public async Task<PartnerOtpIssued> IssueAsync(
        Guid partnerId, Guid jeeberId, double amount, CancellationToken ct)
    {
        // Cryptographically-random, zero-padded to the full digit width (leading zeros preserved).
        var code = RandomNumberGenerator.GetInt32(0, CodeExclusiveMax)
            .ToString(CodeFormat, CultureInfo.InvariantCulture);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(PartnerOtpChallengePolicy.TtlSeconds);
        var challengeId = await _store.IssueAsync(partnerId, jeeberId, amount, HashCode(code), expiresAt, ct);
        return new PartnerOtpIssued(challengeId, PartnerOtpChallengePolicy.TtlSeconds, code);
    }

    public Task<PartnerOtpValidation> VerifyAsync(
        Guid challengeId, Guid partnerId, Guid jeeberId, double amount, string code, CancellationToken ct)
        => _store.ValidateAndConsumeAsync(challengeId, partnerId, jeeberId, amount, HashCode(code ?? string.Empty), ct);

    /// <summary>SHA-256 hex of the code (uppercase). The raw code is never persisted or logged.</summary>
    private static string HashCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
