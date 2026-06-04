using FluentAssertions;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// ADR-002 §3 / PR-2 (owner-approved 2026-06-04) tests for the additive
/// deprecated-alias dual-read layer. The load-bearing test is
/// <see cref="InFlight_HeadingOff_Row_Accepts_Canonical_Transition_During_Window"/>:
/// a row persisted under the OLD vocabulary must keep transitioning during the
/// deprecation window so a running delivery never 422s mid-flight (D4).
/// </summary>
public class DeliveryStatusAliasTests
{
    // ----- Frozen ADR-002 §3 alias map ---------------------------------------

    [Theory]
    [InlineData(RequestStatus.PickedUp, CanonicalDeliveryStatus.Picked)]
    [InlineData(RequestStatus.HeadingOff, CanonicalDeliveryStatus.InTransit)]
    [InlineData(RequestStatus.AtDoor, CanonicalDeliveryStatus.AtDoor)]
    [InlineData(RequestStatus.Delivered, CanonicalDeliveryStatus.Done)]
    [InlineData(RequestStatus.Cancelled, CanonicalDeliveryStatus.Cancelled)]
    [InlineData(RequestStatus.Expired, CanonicalDeliveryStatus.Expired)]
    [InlineData(RequestStatus.Disputed, CanonicalDeliveryStatus.FailedNeedsEscalation)]
    [InlineData(RequestStatus.Rated, CanonicalDeliveryStatus.Done)]
    [InlineData(RequestStatus.Accepted, CanonicalDeliveryStatus.Ordered)]
    public void Legacy_Token_Dual_Reads_To_Canonical(string legacy, string canonical)
    {
        DeliveryStatusAlias.ToCanonical(legacy).Should().Be(canonical);
    }

    // ----- Dual-read is idempotent on canonical tokens -----------------------

    [Theory]
    [InlineData(CanonicalDeliveryStatus.Ordered)]
    [InlineData(CanonicalDeliveryStatus.Picked)]
    [InlineData(CanonicalDeliveryStatus.InTransit)]
    [InlineData(CanonicalDeliveryStatus.AtDoor)]
    [InlineData(CanonicalDeliveryStatus.Done)]
    [InlineData(CanonicalDeliveryStatus.Cancelled)]
    [InlineData(CanonicalDeliveryStatus.FailedNeedsEscalation)]
    [InlineData(CanonicalDeliveryStatus.Expired)]
    public void Canonical_Token_Resolves_To_Itself(string canonical)
    {
        DeliveryStatusAlias.ToCanonical(canonical).Should().Be(canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("totally_unknown")]
    public void Unknown_Or_Blank_Returns_Null(string? status)
    {
        DeliveryStatusAlias.ToCanonical(status).Should().BeNull();
        DeliveryStatusAlias.CanResolve(status).Should().BeFalse();
    }

    // ----- THE no-regression test (ADR-002 Confirmation §) -------------------

    [Fact]
    public void InFlight_HeadingOff_Row_Accepts_Canonical_Transition_During_Window()
    {
        // A delivery persisted under the OLD vocabulary as 'heading_off'.
        const string persistedLegacy = RequestStatus.HeadingOff;

        // Dual-read resolves it to the canonical InTransit.
        var canonicalFrom = DeliveryStatusAlias.ToCanonical(persistedLegacy);
        canonicalFrom.Should().Be(CanonicalDeliveryStatus.InTransit);

        // The Jeeber taps the next step. Derive the trigger, then validate
        // against the frozen table — the in-flight row must NOT 422.
        var trigger = DeliverySm.DeriveTrigger(
            canonicalFrom!, CanonicalDeliveryStatus.AtDoor, DeliveryTriggerSource.Jeeber);
        trigger.Should().Be(DeliveryTrigger.JeeberTap);

        var result = DeliverySm.ValidateExplicit(
            canonicalFrom!, trigger!, CanonicalDeliveryStatus.AtDoor);
        result.IsValid.Should().BeTrue("an in-flight heading_off row must keep flowing during the deprecation window");
        result.To.Should().Be(CanonicalDeliveryStatus.AtDoor);
    }

    [Fact]
    public void InFlight_PickedUp_Row_Accepts_Canonical_Transition_During_Window()
    {
        var canonicalFrom = DeliveryStatusAlias.ToCanonical(RequestStatus.PickedUp);
        canonicalFrom.Should().Be(CanonicalDeliveryStatus.Picked);

        var result = DeliverySm.Validate(canonicalFrom!, DeliveryTrigger.JeeberTap);
        result.IsValid.Should().BeTrue();
        result.To.Should().Be(CanonicalDeliveryStatus.InTransit);
    }

    // ----- Drain observability ----------------------------------------------

    [Theory]
    [InlineData(RequestStatus.PickedUp)]
    [InlineData(RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.Delivered)]
    [InlineData(RequestStatus.Disputed)]
    [InlineData(RequestStatus.Rated)]
    [InlineData(RequestStatus.Accepted)]
    public void Deprecated_Tokens_Are_Flagged_For_Drain(string deprecated)
    {
        DeliveryStatusAlias.IsDeprecated(deprecated).Should().BeTrue();
    }

    [Theory]
    [InlineData(CanonicalDeliveryStatus.Picked)]
    [InlineData(CanonicalDeliveryStatus.InTransit)]
    [InlineData(CanonicalDeliveryStatus.Done)]
    [InlineData(RequestStatus.AtDoor)]      // pure normalization, not "old vocabulary"
    [InlineData(RequestStatus.Cancelled)]   // pure normalization
    public void Canonical_And_Normalized_Tokens_Are_Not_Flagged_For_Drain(string token)
    {
        DeliveryStatusAlias.IsDeprecated(token).Should().BeFalse();
    }
}
