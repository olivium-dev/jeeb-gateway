using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JeebGateway.Partner;
using JeebGateway.StateService.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class PartnerStateServiceStoreTests
{
    private static readonly Guid PartnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JeeberId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task WalletOperation_FirstClaimCompletesAndReplays()
    {
        var store = WalletStore();
        var key = new PartnerOperationKey(PartnerOperationType.Topup, PartnerId, "idem-12345678");
        var intent = new PartnerOperationIntent(PartnerId, JeeberId, 25d, null);
        var result = new PartnerWalletMoveResponse
        {
            TransactionId = Guid.NewGuid(),
            Amount = 25d,
            Fees = 1d,
        };

        (await store.TryClaimAsync(key, intent, default)).Kind.Should().Be(PartnerClaimKind.Won);
        await store.CompleteAsync(key, result.TransactionId, result, default);

        var replay = await store.TryClaimAsync(key, intent, default);
        replay.Kind.Should().Be(PartnerClaimKind.Replay);
        replay.Result.Should().BeEquivalentTo(result);
    }

    [Fact]
    public async Task WalletOperation_ReleasedPreCommitClaimCanBeReclaimedOnce()
    {
        var store = WalletStore();
        var key = new PartnerOperationKey(PartnerOperationType.Topup, PartnerId, "idem-retry-1234");
        var intent = new PartnerOperationIntent(PartnerId, JeeberId, 25d, null);

        (await store.TryClaimAsync(key, intent, default)).Kind.Should().Be(PartnerClaimKind.Won);
        await store.ReleaseAsync(key, default);

        (await store.TryClaimAsync(key, intent, default)).Kind.Should().Be(PartnerClaimKind.Won);
        (await store.TryClaimAsync(key, intent, default)).Kind.Should().Be(PartnerClaimKind.InFlight);
    }

    [Fact]
    public async Task OtpChallenge_FiveWrongAttemptsExhaustTheChallenge()
    {
        var store = OtpStore();
        var id = await store.IssueAsync(
            PartnerId,
            JeeberId,
            75d,
            Hash("123456"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            default);

        for (var attempt = 1; attempt <= PartnerOtpChallengePolicy.MaxAttempts; attempt++)
        {
            var verdict = await store.ValidateAndConsumeAsync(
                id, PartnerId, JeeberId, 75d, Hash("000000"), default);
            verdict.Outcome.Should().Be(PartnerOtpOutcome.WrongCode);
            verdict.AttemptsRemaining.Should().Be(PartnerOtpChallengePolicy.MaxAttempts - attempt);
        }

        var exhausted = await store.ValidateAndConsumeAsync(
            id, PartnerId, JeeberId, 75d, Hash("123456"), default);
        exhausted.Outcome.Should().Be(PartnerOtpOutcome.Exhausted);
    }

    [Fact]
    public async Task OtpChallenge_ConcurrentCorrectSubmitsConsumeExactlyOnce()
    {
        var store = OtpStore();
        var id = await store.IssueAsync(
            PartnerId,
            JeeberId,
            75d,
            Hash("123456"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            default);

        var submits = await Task.WhenAll(
            store.ValidateAndConsumeAsync(id, PartnerId, JeeberId, 75d, Hash("123456"), default),
            store.ValidateAndConsumeAsync(id, PartnerId, JeeberId, 75d, Hash("123456"), default));

        submits.Select(x => x.Outcome).Should().ContainSingle(x => x == PartnerOtpOutcome.Valid);
        submits.Select(x => x.Outcome).Should().ContainSingle(x => x == PartnerOtpOutcome.Consumed);
    }

    private static StateServicePartnerWalletOperationStore WalletStore()
        => new(
            new InMemoryIdempotencyStore(TimeProvider.System),
            NullLogger<StateServicePartnerWalletOperationStore>.Instance);

    private static StateServicePartnerOtpChallengeStore OtpStore()
        => new(new InMemoryIdempotencyStore(TimeProvider.System), TimeProvider.System);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
