using System;
using System.Collections.Generic;
using FluentAssertions;
using JeebGateway.Ratings;
using JeebGateway.Ratings.Jeeb;
using JeebGateway.service.ServiceFeedback;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1489 — unit coverage of the Jeeb-domain rating vocabulary + the generic ->
/// Jeeb <c>{state, ...}</c> projection that lives in the gateway (GR2). These
/// bypass HTTP/DI so the Jeeb semantics applied over the shared opaque primitive
/// can be exercised directly.
/// </summary>
public class JeebRatingProjectionTests
{
    private static BlindRatingPartyState Submitted(int score, string? comment = "ok")
        => new() { Submitted = true, Score = score, Comment = comment, SubmittedAt = DateTimeOffset.UtcNow };

    private static BlindRatingPartyState NotSubmitted() => new() { Submitted = false };

    [Fact]
    public void CorrelationForDelivery_Is_Opaque_Jeeb_Prefixed_Linkage()
    {
        JeebRatingVocabulary.CorrelationForDelivery("d-42").Should().Be("jeeb:delivery:d-42");
    }

    [Fact]
    public void CorrelationForDelivery_Rejects_Blank()
    {
        var act = () => JeebRatingVocabulary.CorrelationForDelivery("  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildTags_Always_Stamps_Partition_And_Role()
    {
        var sami = JeebRatingVocabulary.BuildTags(JeebRatingRole.Sami, null);
        sami.Should().Contain(JeebRatingVocabulary.PartitionValue);
        sami.Should().Contain("role:sami");

        var kamal = JeebRatingVocabulary.BuildTags(JeebRatingRole.Kamal, null);
        kamal.Should().Contain("role:kamal");
    }

    [Fact]
    public void BuildTags_Accepts_Whitelisted_Tags_And_Rejects_Others()
    {
        var ok = JeebRatingVocabulary.BuildTags(JeebRatingRole.Sami, new[] { "punctuality", "COURTESY" });
        ok.Should().Contain("punctuality");
        ok.Should().Contain("courtesy");

        var act = () => JeebRatingVocabulary.BuildTags(JeebRatingRole.Sami, new[] { "delivery" });
        act.Should().Throw<ArgumentException>("'delivery' is not a recognised Jeeb rating tag");
    }

    [Fact]
    public void RoleFor_Maps_Client_To_Sami_And_Jeeber_To_Kamal()
    {
        JeebRatingVocabulary.RoleFor(callerIsClient: true).Should().Be(JeebRatingRole.Sami);
        JeebRatingVocabulary.RoleFor(callerIsClient: false).Should().Be(JeebRatingRole.Kamal);
    }

    [Fact]
    public void Project_PendingMine_When_Viewer_Has_Not_Submitted()
    {
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-1",
            Revealed = false,
            SubmittedCount = 0,
            Self = NotSubmitted(),
            Counterparty = NotSubmitted(),
        };

        var view = JeebRatingProjection.Project("d-1", JeebRatingRole.Sami, upstream);

        view.State.Should().Be(RatingStateCodes.PendingMine);
        view.Role.Should().Be("sami");
        view.Mine.Submitted.Should().BeFalse();
        view.Theirs.Submitted.Should().BeFalse();
    }

    [Fact]
    public void Project_PendingTheirs_Hides_Counterparty_When_Only_Viewer_Submitted()
    {
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-2",
            Revealed = false,
            SubmittedCount = 1,
            Self = Submitted(5, "great"),
            // Generic primitive withholds counterparty details while blind.
            Counterparty = new BlindRatingPartyState { Submitted = true },
        };

        var view = JeebRatingProjection.Project("d-2", JeebRatingRole.Kamal, upstream);

        view.State.Should().Be(RatingStateCodes.PendingTheirs);
        view.Mine.Stars.Should().Be(5);
        view.Theirs.Submitted.Should().BeTrue();
        view.Theirs.Stars.Should().BeNull("counterparty detail must stay blind until revealed");
    }

    [Fact]
    public void Project_Revealed_Exposes_Both_Sides()
    {
        var when = DateTimeOffset.UtcNow;
        var upstream = new BlindRevealStateResponse
        {
            CorrelationId = "jeeb:delivery:d-3",
            Revealed = true,
            RevealedAt = when,
            SubmittedCount = 2,
            Self = Submitted(4, "mine"),
            Counterparty = Submitted(3, "theirs"),
        };

        var view = JeebRatingProjection.Project("d-3", JeebRatingRole.Sami, upstream);

        view.State.Should().Be(RatingStateCodes.Revealed);
        view.Revealed.Should().BeTrue();
        view.RevealedAt.Should().Be(when);
        view.Mine.Stars.Should().Be(4);
        view.Theirs.Stars.Should().Be(3);
        view.Theirs.Comment.Should().Be("theirs");
    }
}
