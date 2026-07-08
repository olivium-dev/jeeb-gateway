using FluentAssertions;
using JeebGateway.Ratings;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-020 / JEEB-38 — unit coverage of the pure reveal logic in
/// <see cref="BlindRevealPolicy"/>. These tests bypass HTTP / DI / stores
/// so the rule combinatorics can be exercised exhaustively.
/// </summary>
public class BlindRevealPolicyTests
{
    private static readonly DateTimeOffset DeliveredAt = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    private static RatingEntry ClientEntry(int stars = 5, string? comment = "great")
        => new("client-1", stars, comment, DeliveredAt.AddMinutes(5));

    private static RatingEntry JeeberEntry(int stars = 4, string? comment = "ok")
        => new("jeeber-1", stars, comment, DeliveredAt.AddMinutes(10));

    [Fact]
    public void Caller_Without_Rating_Sees_PendingMine_With_Both_Sides_Hidden()
    {
        var view = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt.AddHours(1),
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: null,
            jeeberRating: null,
            ratingWindow: Window);

        view.Outcome.Should().Be(BlindRevealOutcome.PendingMine);
        view.MyRating.Should().BeNull();
        view.TheirRating.Should().BeNull();
        view.WindowExpired.Should().BeFalse();
        view.WindowClosesAt.Should().Be(DeliveredAt + Window);
    }

    [Fact]
    public void Counterparty_Rating_Stays_Hidden_When_Only_Counterparty_Has_Submitted()
    {
        // Jeeber submitted; Client (the caller) has not yet. Caller must NOT
        // see the Jeeber's rating — that would defeat the blind requirement.
        var view = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt.AddHours(2),
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: null,
            jeeberRating: JeeberEntry(),
            ratingWindow: Window);

        view.Outcome.Should().Be(BlindRevealOutcome.PendingMine);
        view.MyRating.Should().BeNull();
        view.TheirRating.Should().BeNull("counterparty rating must stay blind while caller has not submitted");
    }

    [Fact]
    public void Caller_Sees_Own_Rating_But_Not_Counterparty_When_Only_Caller_Submitted()
    {
        var clientRating = ClientEntry();
        var view = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt.AddHours(1),
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: clientRating,
            jeeberRating: null,
            ratingWindow: Window);

        view.Outcome.Should().Be(BlindRevealOutcome.PendingTheirs);
        view.MyRating.Should().Be(clientRating);
        view.TheirRating.Should().BeNull();
        view.WindowExpired.Should().BeFalse();
    }

    [Fact]
    public void Both_Sides_Submitted_Reveals_Both_Ratings_To_Each_Party()
    {
        var client = ClientEntry(stars: 5);
        var jeeber = JeeberEntry(stars: 3);

        var clientView = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt.AddHours(3),
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: client,
            jeeberRating: jeeber,
            ratingWindow: Window);

        clientView.Outcome.Should().Be(BlindRevealOutcome.Revealed);
        clientView.MyRating.Should().Be(client);
        clientView.TheirRating.Should().Be(jeeber);

        var jeeberView = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt.AddHours(3),
            deliveredAt: DeliveredAt,
            callerIsClient: false,
            clientRating: client,
            jeeberRating: jeeber,
            ratingWindow: Window);

        jeeberView.Outcome.Should().Be(BlindRevealOutcome.Revealed);
        jeeberView.MyRating.Should().Be(jeeber);
        jeeberView.TheirRating.Should().Be(client);
    }

    [Fact]
    public void Window_Closure_Locks_Row_When_Neither_Side_Submitted()
    {
        var view = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt + Window + TimeSpan.FromMinutes(1),
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: null,
            jeeberRating: null,
            ratingWindow: Window);

        view.Outcome.Should().Be(BlindRevealOutcome.LockedNoRating);
        view.WindowExpired.Should().BeTrue();
        view.MyRating.Should().BeNull();
        view.TheirRating.Should().BeNull();
    }

    [Fact]
    public void Window_Closure_With_One_Side_Submitted_Does_Not_Reveal()
    {
        // Client rated within the window; Jeeber missed the window.
        // After expiry the locked-no-rating outcome surfaces without exposing
        // the one-sided rating to either party.
        var client = ClientEntry();
        var view = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt + Window + TimeSpan.FromHours(1),
            deliveredAt: DeliveredAt,
            callerIsClient: false, // jeeber querying after the window closed
            clientRating: client,
            jeeberRating: null,
            ratingWindow: Window);

        view.Outcome.Should().Be(BlindRevealOutcome.LockedNoRating);
        view.WindowExpired.Should().BeTrue();
        view.MyRating.Should().BeNull();
        view.TheirRating.Should().BeNull("one-sided ratings are never revealed after expiry");
    }

    [Fact]
    public void Both_Submitted_Stays_Revealed_Even_After_Window_Closure()
    {
        var client = ClientEntry();
        var jeeber = JeeberEntry();
        var view = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt + Window + TimeSpan.FromDays(30),
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: client,
            jeeberRating: jeeber,
            ratingWindow: Window);

        view.Outcome.Should().Be(BlindRevealOutcome.Revealed,
            "both ratings remain visible to both parties indefinitely once both have submitted");
        view.WindowExpired.Should().BeTrue();
        view.MyRating.Should().Be(client);
        view.TheirRating.Should().Be(jeeber);
    }

    [Fact]
    public void Window_Boundary_Is_Inclusive_At_Closes_At()
    {
        // At exactly deliveredAt + window the policy still considers the
        // window open (not yet > windowClosesAt) so submissions are still
        // valid up to and including the boundary instant.
        var view = BlindRevealPolicy.ProjectFor(
            now: DeliveredAt + Window,
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: null,
            jeeberRating: null,
            ratingWindow: Window);

        view.Outcome.Should().Be(BlindRevealOutcome.PendingMine);
        view.WindowExpired.Should().BeFalse();
    }
}
