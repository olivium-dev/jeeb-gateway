using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.IdentityReveal;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Feature <c>double-blind-reveal</c> — unit coverage of the pure server-owned
/// reveal ladder in <see cref="IdentityRevealPolicy"/>. Bypasses HTTP / DI / stores
/// so the status → level mapping can be asserted exhaustively.
/// </summary>
public class IdentityRevealPolicyTests
{
    [Theory]
    [InlineData(RequestStatus.Scheduled)]
    [InlineData(RequestStatus.Pending)]
    [InlineData(RequestStatus.Matched)]
    public void PreMatch_States_Are_Hidden(string status)
    {
        IdentityRevealPolicy.LevelFor(status, counterpartBound: true)
            .Should().Be(IdentityRevealLevel.Hidden);
    }

    [Theory]
    [InlineData(RequestStatus.Accepted)]
    [InlineData(RequestStatus.Delivered)]
    [InlineData(RequestStatus.Rated)]
    [InlineData(RequestStatus.CancellationRequested)]
    [InlineData(RequestStatus.Disputed)]
    public void Bound_NonCustody_States_Show_NamePreview_Without_Phone(string status)
    {
        var level = IdentityRevealPolicy.LevelFor(status, counterpartBound: true);

        level.Should().Be(IdentityRevealLevel.NamePreview);
        IdentityRevealPolicy.IsNameVisible(level).Should().BeTrue();
        IdentityRevealPolicy.IsPhoneVisible(level).Should().BeFalse();
    }

    [Theory]
    [InlineData(RequestStatus.PickedUp)]
    [InlineData(RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.AtDoor)]
    public void InCustody_States_Are_Contactable_With_Phone(string status)
    {
        var level = IdentityRevealPolicy.LevelFor(status, counterpartBound: true);

        level.Should().Be(IdentityRevealLevel.Contactable);
        IdentityRevealPolicy.IsNameVisible(level).Should().BeTrue();
        IdentityRevealPolicy.IsPhoneVisible(level).Should().BeTrue();
    }

    [Theory]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Expired)]
    public void Terminal_Negative_States_Are_Hidden(string status)
    {
        IdentityRevealPolicy.LevelFor(status, counterpartBound: true)
            .Should().Be(IdentityRevealLevel.Hidden);
    }

    [Fact]
    public void Unbound_Counterpart_Is_Always_Hidden_Even_At_Contactable_Status()
    {
        // Defends against a status/row mismatch — no counterpart bound ⇒ nothing
        // can be revealed regardless of how far the status has advanced.
        IdentityRevealPolicy.LevelFor(RequestStatus.AtDoor, counterpartBound: false)
            .Should().Be(IdentityRevealLevel.Hidden);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("some_unknown_status")]
    public void Empty_Or_Unknown_Status_Defaults_To_Hidden(string? status)
    {
        IdentityRevealPolicy.LevelFor(status!, counterpartBound: true)
            .Should().Be(IdentityRevealLevel.Hidden);
    }
}
