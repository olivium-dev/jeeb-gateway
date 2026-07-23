using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.Partner;

/// <summary>
/// Stateless gateway adapter for partner step-up challenges. Challenge hashes, bounded-attempt
/// reservations, and single-use consumption markers live in jeeb-state-service's atomic
/// insert-or-return KV; the gateway holds no challenge state.
/// </summary>
public sealed class StateServicePartnerOtpChallengeStore : IPartnerOtpChallengeStore
{
    private const double AmountEpsilon = 0.00005d;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _state;
    private readonly TimeProvider _clock;

    public StateServicePartnerOtpChallengeStore(IIdempotencyStore state, TimeProvider clock)
    {
        _state = state;
        _clock = clock;
    }

    public async Task<Guid> IssueAsync(
        Guid partnerId,
        Guid jeeberId,
        double amount,
        string codeHash,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var row = new StoredChallenge(partnerId, jeeberId, amount, codeHash, expiresAt);
        var body = JsonSerializer.Serialize(row, Json);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var id = Guid.NewGuid();
            var outcome = await _state.PutOrGetAsync(
                ChallengeKey(id), 201, body, RemainingTtl(expiresAt), ct);
            if (outcome.Inserted)
            {
                return id;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique partner OTP challenge id.");
    }

    public async Task<PartnerOtpValidation> ValidateAndConsumeAsync(
        Guid challengeId,
        Guid partnerId,
        Guid jeeberId,
        double amount,
        string codeHash,
        CancellationToken ct)
    {
        var stored = await _state.GetAsync(ChallengeKey(challengeId), ct);
        var challenge = Deserialize(stored?.ResponseBodyJson);
        if (challenge is null)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.NotFound, 0);
        }

        if (challenge.PartnerId != partnerId
            || challenge.JeeberId != jeeberId
            || Math.Abs(challenge.Amount - amount) >= AmountEpsilon)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.Mismatch, 0);
        }

        if (_clock.GetUtcNow() >= challenge.ExpiresAt)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.Expired, 0);
        }

        if (await _state.GetAsync(ConsumedKey(challengeId), ct) is not null)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.Consumed, 0);
        }

        var slot = await ReserveAttemptAsync(challengeId, challenge.ExpiresAt, ct);
        if (slot == 0)
        {
            return await _state.GetAsync(ConsumedKey(challengeId), ct) is not null
                ? new PartnerOtpValidation(PartnerOtpOutcome.Consumed, 0)
                : new PartnerOtpValidation(PartnerOtpOutcome.Exhausted, 0);
        }

        if (!HashEquals(challenge.CodeHash, codeHash))
        {
            return new PartnerOtpValidation(
                PartnerOtpOutcome.WrongCode,
                PartnerOtpChallengePolicy.MaxAttempts - slot);
        }

        var consumed = await _state.PutOrGetAsync(
            ConsumedKey(challengeId), 200, "{}", RemainingTtl(challenge.ExpiresAt), ct);
        return consumed.Inserted
            ? new PartnerOtpValidation(PartnerOtpOutcome.Valid, 0)
            : new PartnerOtpValidation(PartnerOtpOutcome.Consumed, 0);
    }

    private async Task<int> ReserveAttemptAsync(
        Guid challengeId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        for (var slot = 1; slot <= PartnerOtpChallengePolicy.MaxAttempts; slot++)
        {
            var reservation = await _state.PutOrGetAsync(
                AttemptKey(challengeId, slot), 200, "{}", RemainingTtl(expiresAt), ct);
            if (reservation.Inserted)
            {
                return slot;
            }
        }

        return 0;
    }

    private int RemainingTtl(DateTimeOffset expiresAt)
    {
        var seconds = (int)Math.Ceiling((expiresAt - _clock.GetUtcNow()).TotalSeconds);
        return Math.Max(1, seconds);
    }

    private static StoredChallenge? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredChallenge>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HashEquals(string storedHash, string presentedHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(presentedHash));

    private static string ChallengeKey(Guid id) => $"partner-otp-challenge:{id:N}";
    private static string AttemptKey(Guid id, int slot) => $"partner-otp-challenge:{id:N}:attempt:{slot}";
    private static string ConsumedKey(Guid id) => $"partner-otp-challenge:{id:N}:consumed";

    private sealed record StoredChallenge(
        Guid PartnerId,
        Guid JeeberId,
        double Amount,
        string CodeHash,
        DateTimeOffset ExpiresAt);
}
