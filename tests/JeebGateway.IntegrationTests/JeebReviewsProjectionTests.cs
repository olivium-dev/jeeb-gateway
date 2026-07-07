using System;
using FluentAssertions;
using JeebGateway.JeebReviews;
using JeebGateway.service.ServiceFeedback;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit coverage of the generic→Jeeb reviews/ratings-FAMILY projection that lives in
/// the gateway (ADR-0001 thin map): the cold-start empty reviews page used while the
/// generic feedback-service has no per-jeeber reviews LIST, and the
/// <c>{ deliveryId, state, ratings, ratedCount }</c> status envelope the mobile
/// <c>RatingStatus.fromJson</c> parser reads. These bypass HTTP/DI — mirroring
/// <see cref="JeebRatingProjectionTests"/> / <see cref="JeebWalletProjectionTests"/>.
/// </summary>
public class JeebReviewsProjectionTests
{
    private static BlindRatingPartyState Submitted(int score, string? comment = "ok")
        => new() { Submitted = true, Score = score, Comment = comment, SubmittedAt = DateTimeOffset.UtcNow };

    private static BlindRatingPartyState NotSubmitted() => new() { Submitted = false };

    // ── reviews list (coverage-gap fallback) ──────────────────────────────────────

    [Fact]
    public void EmptyReviewsPage_Is_Cold_Start_With_Hidden_Aggregate()
    {
        var page = JeebReviewsProjection.EmptyReviewsPage("jeeber-7", page: 1, pageSize: 20);

        page.JeeberId.Should().Be("jeeber-7");
        page.Items.Should().BeEmpty();
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().Be(0);
        page.TotalPages.Should().Be(1);
        page.ColdStart.Should().BeTrue();
        page.ReviewCount.Should().Be(0);
        // D59 — averageScore is null while cold-start so the mobile parser hides it.
        page.AverageScore.Should().BeNull();
    }

    [Fact]
    public void EmptyReviewsPage_Clamps_NonPositive_Paging_To_Safe_Defaults()
    {
        var page = JeebReviewsProjection.EmptyReviewsPage("j", page: 0, pageSize: 0);

        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
    }

    // ── status envelope (state mapping) ───────────────────────────────────────────

    [Fact]
    public void ProjectStatus_PendingBoth_When_Neither_Submitted()
    {
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-1",
            Revealed = false,
            Self = NotSubmitted(),
            Counterparty = NotSubmitted(),
        };

        var view = JeebReviewsProjection.ProjectStatus("d-1", upstream);

        view.DeliveryId.Should().Be("d-1");
        view.State.Should().Be(JeebReviewsProjection.StatusCodes.PendingBoth);
        view.RatedCount.Should().Be(0);
        view.Ratings.Should().BeEmpty();
    }

    [Fact]
    public void ProjectStatus_PendingCounter_When_Only_Viewer_Submitted()
    {
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-2",
            Revealed = false,
            Self = Submitted(5, "great"),
            Counterparty = NotSubmitted(),
        };

        var view = JeebReviewsProjection.ProjectStatus("d-2", upstream);

        view.State.Should().Be(JeebReviewsProjection.StatusCodes.PendingCounter);
        view.RatedCount.Should().Be(1);
        // Counterparty stays blind — no ratings row exposed until revealed.
        view.Ratings.Should().BeEmpty();
    }

    [Fact]
    public void ProjectStatus_PendingSelf_When_Only_Counterparty_Submitted()
    {
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-3",
            Revealed = false,
            Self = NotSubmitted(),
            Counterparty = new BlindRatingPartyState { Submitted = true },
        };

        var view = JeebReviewsProjection.ProjectStatus("d-3", upstream);

        view.State.Should().Be(JeebReviewsProjection.StatusCodes.PendingSelf);
        view.RatedCount.Should().Be(1);
        view.Ratings.Should().BeEmpty("counterparty detail must stay blind until revealed");
    }

    [Fact]
    public void ProjectStatus_Revealed_Exposes_Counterparty_Rating_Row()
    {
        // Mutual reveal requires both sides to rate. SubmittedCount = 2 prevents
        // stale/invalid one-sided reveal data from being treated as visible.
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-4",
            Revealed = true,
            RevealedAt = DateTimeOffset.UtcNow,
            SubmittedCount = 2,
            Self = Submitted(4, "mine"),
            Counterparty = Submitted(3, "theirs"),
        };

        var view = JeebReviewsProjection.ProjectStatus("d-4", upstream);

        view.State.Should().Be(JeebReviewsProjection.StatusCodes.Revealed);
        view.RatedCount.Should().Be(2);
        view.Ratings.Should().HaveCount(1);
        view.Ratings[0].Score.Should().Be(3);
        view.Ratings[0].Comment.Should().Be("theirs");
    }

    [Fact]
    public void ProjectStatus_LockedNoRating_When_Window_Expired_With_One_Side_Rated()
    {
        // No one-sided auto-reveal: stale upstream rows that claim revealed with
        // fewer than 2 submissions are projected as locked_no_rating and expose no
        // rating rows.
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-5",
            Revealed = true,
            RevealedAt = DateTimeOffset.UtcNow,
            SubmittedCount = 1,
            Self = NotSubmitted(),
            Counterparty = Submitted(2, "theirs-never-revealed"),
        };

        var view = JeebReviewsProjection.ProjectStatus("d-5", upstream);

        view.State.Should().Be(JeebReviewsProjection.StatusCodes.LockedNoRating);
        view.RatedCount.Should().Be(1);
        view.Ratings.Should().BeEmpty();
    }
}
