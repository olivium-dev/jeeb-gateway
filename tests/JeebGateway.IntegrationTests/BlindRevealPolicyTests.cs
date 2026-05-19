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
    public void Window_Closure_Reveals_Whatever_Side_Already_Submitted()
    {
        // Client rated within the window; Jeeber missed the window.
        // After expiry the locked-no-rating outcome surfaces; the existing
        // side is visible to both parties (no point hiding it once locked).
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
        view.MyRating.Should().BeNull("jeeber never submitted");
        view.TheirRating.Should().Be(client);
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

    // -----------------------------------------------------------------
    // T-BE-025 / JEB-61 — auto_revealed outcome
    // -----------------------------------------------------------------

    [Fact]
    public void AutoReveal_Stamp_Promotes_LockedNoRating_To_AutoRevealed_With_Existing_Side_Visible()
    {
        // Sami (jeeber) rated within the window; Client never did.
        // After the JEB-61 cron stamps AutoRevealedAt, both sides see
        // state=auto_revealed; client's stars stay null per the spec
        // ("do NOT auto-fill any score").
        var jeeber = JeeberEntry(stars: 4);
        var stamp = DeliveredAt + Window + TimeSpan.FromHours(2);
        var view = BlindRevealPolicy.ProjectFor(
            now: stamp,
            deliveredAt: DeliveredAt,
            callerIsClient: true, // client querying
            clientRating: null,
            jeeberRating: jeeber,
            ratingWindow: Window,
            autoRevealedAt: stamp);

        view.Outcome.Should().Be(BlindRevealOutcome.AutoRevealed);
        view.MyRating.Should().BeNull("client never submitted — no synthetic stars");
        view.TheirRating.Should().Be(jeeber);
        view.WindowExpired.Should().BeTrue();
    }

    [Fact]
    public void AutoReveal_With_Neither_Side_Submitted_Yields_AutoRevealed_With_Both_Null()
    {
        var stamp = DeliveredAt + Window + TimeSpan.FromMinutes(30);
        var view = BlindRevealPolicy.ProjectFor(
            now: stamp,
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: null,
            jeeberRating: null,
            ratingWindow: Window,
            autoRevealedAt: stamp);

        view.Outcome.Should().Be(BlindRevealOutcome.AutoRevealed);
        view.MyRating.Should().BeNull();
        view.TheirRating.Should().BeNull();
        view.WindowExpired.Should().BeTrue();
    }

    [Fact]
    public void Mutual_Reveal_Wins_Over_Auto_Reveal_Stamp()
    {
        // Defensive: if for any reason BOTH ratings exist AND AutoRevealedAt
        // is non-null (shouldn't happen because the cron skips
        // both-submitted rows), the mutual-consent outcome wins so the
        // wire payload remains "revealed", not "auto_revealed".
        var client = ClientEntry();
        var jeeber = JeeberEntry();
        var stamp = DeliveredAt + Window + TimeSpan.FromHours(2);

        var view = BlindRevealPolicy.ProjectFor(
            now: stamp,
            deliveredAt: DeliveredAt,
            callerIsClient: true,
            clientRating: client,
            jeeberRating: jeeber,
            ratingWindow: Window,
            autoRevealedAt: stamp);

        view.Outcome.Should().Be(BlindRevealOutcome.Revealed);
    }

    [Fact]
    public void RatingStateCodes_For_AutoRevealed_Maps_To_Auto_Revealed_String()
    {
        RatingStateCodes.For(BlindRevealOutcome.AutoRevealed)
            .Should().Be("auto_revealed");
    }
}
