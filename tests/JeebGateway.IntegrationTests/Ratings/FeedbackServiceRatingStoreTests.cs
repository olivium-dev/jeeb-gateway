using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Ratings;
using JeebGateway.Ratings.Jeeb;
using JeebGateway.service.ServiceFeedback;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FeedbackApiException = JeebGateway.service.ServiceFeedback.ApiException;

namespace JeebGateway.IntegrationTests.Ratings;

/// <summary>
/// Gap 8 — coverage of <see cref="FeedbackServiceRatingStore"/>, the
/// feedback-service-backed <see cref="IRatingStore"/> that becomes the
/// delivery-ratings record-of-truth when <c>FeatureFlags:UseUpstream:Ratings</c>
/// is ON. These exercise the store against a STUBBED
/// <see cref="ServiceFeedbackClient"/> (its blind-ratings methods are virtual) and
/// assert:
///   * submit forwards the correct opaque (correlationId, raterId, rateeId, score,
///     tags) tuple, with the SAME correlationId the /v1/ratings/jeeb/* surface uses
///     (so both surfaces hit the same upstream row);
///   * reveal projects back onto a RatingPair that drives BlindRevealPolicy to the
///     same outcomes as the in-memory store (parity), including the blind window;
///   * duplicate submit (upstream 404/400 idempotency) maps to the in-memory store's
///     "already rated" InvalidOperationException so RatingService -> AlreadyRated;
///   * StableGuid is deterministic and passes a real GUID through unchanged.
/// </summary>
public class FeedbackServiceRatingStoreTests
{
    private static readonly DateTimeOffset DeliveredAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    [Fact]
    public async Task Submit_Forwards_Correlation_Rater_Ratee_And_Tags()
    {
        var stub = new StubFeedback();
        var store = BuildStore(stub);

        await store.EnsureAsync("d-100", "client-1", "jeeber-9", DeliveredAt, CancellationToken.None);
        await store.SubmitAsync(
            "d-100", callerIsClient: true,
            new RatingEntry("client-1", Stars: 5, Comment: "great", SubmittedAt: DeliveredAt.AddHours(1)),
            CancellationToken.None);

        stub.LastSubmit.Should().NotBeNull();
        stub.LastSubmit!.CorrelationId.Should().Be(JeebRatingVocabulary.CorrelationForDelivery("d-100"));
        stub.LastSubmit.CorrelationId.Should().Be("jeeb:delivery:d-100");
        stub.LastSubmit.RaterId.Should().Be(FeedbackServiceRatingStore.StableGuid("client-1"));
        stub.LastSubmit.RateeId.Should().Be(FeedbackServiceRatingStore.StableGuid("jeeber-9"));
        stub.LastSubmit.Score.Should().Be(5);
        stub.LastSubmit.Comment.Should().Be("great");
        // Partition + role tag stamped exactly like the /v1/ratings/jeeb/* surface.
        stub.LastSubmit.Tags.Should().Contain(JeebRatingVocabulary.PartitionValue);
        stub.LastSubmit.Tags.Should().Contain("role:sami"); // client => Sami
    }

    [Fact]
    public async Task Reveal_Projects_Both_Sides_When_Both_Submitted()
    {
        // Upstream reveal: both submitted, scores visible (revealed).
        var stub = new StubFeedback
        {
            Reveal = new BlindRevealStateResponse
            {
                Revealed = true,
                SubmittedCount = 2,
                Self = new BlindRatingPartyState { Submitted = true, Score = 4, Comment = "client-say" },
                Counterparty = new BlindRatingPartyState { Submitted = true, Score = 5, Comment = "jeeber-say" },
            },
        };
        var store = BuildStore(stub);
        await store.EnsureAsync("d-200", "client-1", "jeeber-9", DeliveredAt, CancellationToken.None);

        var pair = await store.GetAsync("d-200", CancellationToken.None);

        pair.Should().NotBeNull();
        // Self was read as the CLIENT viewer, so Self => client side, Counterparty => jeeber side.
        pair!.ClientRating.Should().NotBeNull();
        pair.ClientRating!.Stars.Should().Be(4);
        pair.JeeberRating.Should().NotBeNull();
        pair.JeeberRating!.Stars.Should().Be(5);

        // BlindRevealPolicy over this pair => Revealed for the client caller.
        var view = BlindRevealPolicy.ProjectFor(
            DeliveredAt.AddHours(2), pair.DeliveredAt, callerIsClient: true,
            pair.ClientRating, pair.JeeberRating, Window);
        view.Outcome.Should().Be(BlindRevealOutcome.Revealed);
        view.TheirRating!.Stars.Should().Be(5);
    }

    [Fact]
    public async Task Reveal_Hides_Counterparty_While_Blind_Drives_PendingTheirs()
    {
        // Upstream reveal: client submitted, jeeber not yet — counterparty withheld.
        var stub = new StubFeedback
        {
            Reveal = new BlindRevealStateResponse
            {
                Revealed = false,
                SubmittedCount = 1,
                Self = new BlindRatingPartyState { Submitted = true, Score = 5, Comment = "mine" },
                Counterparty = new BlindRatingPartyState { Submitted = false, Score = null, Comment = null },
            },
        };
        var store = BuildStore(stub);
        await store.EnsureAsync("d-300", "client-1", "jeeber-9", DeliveredAt, CancellationToken.None);

        var pair = await store.GetAsync("d-300", CancellationToken.None);

        pair!.ClientRating.Should().NotBeNull();
        pair.JeeberRating.Should().BeNull("the jeeber has not submitted yet");

        // Client caller: PendingTheirs (sees own, not theirs); window still open.
        var view = BlindRevealPolicy.ProjectFor(
            DeliveredAt.AddHours(1), pair.DeliveredAt, callerIsClient: true,
            pair.ClientRating, pair.JeeberRating, Window);
        view.Outcome.Should().Be(BlindRevealOutcome.PendingTheirs);
        view.MyRating!.Stars.Should().Be(5);
        view.TheirRating.Should().BeNull();
    }

    [Fact]
    public async Task Reveal_404_From_Upstream_Projects_Empty_Pair_PendingMine()
    {
        var stub = new StubFeedback { RevealStatus = 404 };
        var store = BuildStore(stub);
        await store.EnsureAsync("d-400", "client-1", "jeeber-9", DeliveredAt, CancellationToken.None);

        var pair = await store.GetAsync("d-400", CancellationToken.None);

        pair!.ClientRating.Should().BeNull();
        pair.JeeberRating.Should().BeNull();

        var view = BlindRevealPolicy.ProjectFor(
            DeliveredAt.AddHours(1), pair.DeliveredAt, callerIsClient: true,
            pair.ClientRating, pair.JeeberRating, Window);
        view.Outcome.Should().Be(BlindRevealOutcome.PendingMine);
    }

    [Fact]
    public async Task Duplicate_Submit_Maps_To_AlreadyRated_InvalidOperation()
    {
        var stub = new StubFeedback { SubmitStatus = 400 };
        var store = BuildStore(stub);
        await store.EnsureAsync("d-500", "client-1", "jeeber-9", DeliveredAt, CancellationToken.None);

        var act = () => store.SubmitAsync(
            "d-500", callerIsClient: false,
            new RatingEntry("jeeber-9", 3, null, DeliveredAt.AddHours(1)),
            CancellationToken.None);

        // Parity with InMemoryRatingStore: second submit by a party throws
        // InvalidOperationException (RatingService maps that to AlreadyRated).
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Submit_Without_Ensure_Throws_Parity_With_InMemory()
    {
        var store = BuildStore(new StubFeedback());

        var act = () => store.SubmitAsync(
            "never-ensured", callerIsClient: true,
            new RatingEntry("client-1", 4, null, DeliveredAt),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void StableGuid_Is_Deterministic_And_Passes_Through_Real_Guids()
    {
        FeedbackServiceRatingStore.StableGuid("user-x")
            .Should().Be(FeedbackServiceRatingStore.StableGuid("user-x"));
        FeedbackServiceRatingStore.StableGuid("user-x")
            .Should().NotBe(FeedbackServiceRatingStore.StableGuid("user-y"));

        var real = Guid.NewGuid();
        FeedbackServiceRatingStore.StableGuid(real.ToString())
            .Should().Be(real, "a caller that already passes a GUID round-trips unchanged");
    }

    // ----- helpers -----

    private static FeedbackServiceRatingStore BuildStore(ServiceFeedbackClient stub)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => stub);
        var provider = services.BuildServiceProvider();
        return new FeedbackServiceRatingStore(provider.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>
    /// Stub <see cref="ServiceFeedbackClient"/>: overrides the virtual blind-ratings
    /// methods so no HTTP is performed. baseUrl/httpClient are inert.
    /// </summary>
    private sealed class StubFeedback : ServiceFeedbackClient
    {
        public StubFeedback() : base("http://stub.test/", new HttpClient()) { }

        public SubmitBlindRatingRequest? LastSubmit { get; private set; }
        public int SubmitStatus { get; set; } = 200;
        public int RevealStatus { get; set; } = 200;
        public BlindRevealStateResponse Reveal { get; set; } = new()
        {
            Revealed = false,
            SubmittedCount = 0,
            Self = new BlindRatingPartyState { Submitted = false },
            Counterparty = new BlindRatingPartyState { Submitted = false },
        };

        public override Task<SubmitBlindRatingResponse> RatingsSubmitAsync(
            SubmitBlindRatingRequest body, CancellationToken cancellationToken)
        {
            LastSubmit = body;
            if (SubmitStatus != 200)
            {
                throw new FeedbackApiException(
                    "stub submit fault", SubmitStatus, string.Empty,
                    new Dictionary<string, IEnumerable<string>>(), null);
            }
            return Task.FromResult(new SubmitBlindRatingResponse { Id = Guid.NewGuid(), State = Reveal });
        }

        public override Task<BlindRevealStateResponse> RatingsRevealAsync(
            string correlationId, Guid viewerId, CancellationToken cancellationToken)
        {
            if (RevealStatus != 200)
            {
                throw new FeedbackApiException(
                    "stub reveal fault", RevealStatus, string.Empty,
                    new Dictionary<string, IEnumerable<string>>(), null);
            }
            return Task.FromResult(Reveal);
        }
    }
}
