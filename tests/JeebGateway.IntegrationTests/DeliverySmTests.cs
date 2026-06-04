using FluentAssertions;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// ADR-002 (owner-approved 2026-06-04) unit tests for the canonical
/// trigger-keyed <see cref="DeliverySm"/> table, the typed 422 rejection shape,
/// and the gateway-side trigger derivation. These assert the gateway models the
/// SAME 13 in-SM edges as delivery-service <c>status.go</c> (the byte-for-byte
/// parity test lives in <see cref="DeliverySmParityTests"/>, PR-4).
/// </summary>
public class DeliverySmTests
{
    // ----- The 13 explicit in-SM edges (ADR-002 §2.3) are all allowed --------

    [Theory]
    // Ordered row
    [InlineData(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.Picked)]
    [InlineData(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.ClientCancelNoFee, CanonicalDeliveryStatus.Cancelled)]
    [InlineData(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberCancelStrike, CanonicalDeliveryStatus.Cancelled)]
    [InlineData(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.EscalateEither, CanonicalDeliveryStatus.FailedNeedsEscalation)]
    // Picked row
    [InlineData(CanonicalDeliveryStatus.Picked, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.InTransit)]
    [InlineData(CanonicalDeliveryStatus.Picked, DeliveryTrigger.JeeberCancelHighStrike, CanonicalDeliveryStatus.Cancelled)]
    [InlineData(CanonicalDeliveryStatus.Picked, DeliveryTrigger.EscalateEither, CanonicalDeliveryStatus.FailedNeedsEscalation)]
    // InTransit row
    [InlineData(CanonicalDeliveryStatus.InTransit, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.AtDoor)]
    [InlineData(CanonicalDeliveryStatus.InTransit, DeliveryTrigger.EscalateEither, CanonicalDeliveryStatus.FailedNeedsEscalation)]
    // AtDoor row (incl. the escalate_either alias of edge 11)
    [InlineData(CanonicalDeliveryStatus.AtDoor, DeliveryTrigger.OtpVerified, CanonicalDeliveryStatus.Done)]
    [InlineData(CanonicalDeliveryStatus.AtDoor, DeliveryTrigger.OtpFailOrJeeberEscalate, CanonicalDeliveryStatus.FailedNeedsEscalation)]
    [InlineData(CanonicalDeliveryStatus.AtDoor, DeliveryTrigger.EscalateEither, CanonicalDeliveryStatus.FailedNeedsEscalation)]
    // FailedNeedsEscalation row
    [InlineData(CanonicalDeliveryStatus.FailedNeedsEscalation, DeliveryTrigger.AdminResolve, CanonicalDeliveryStatus.Done)]
    [InlineData(CanonicalDeliveryStatus.FailedNeedsEscalation, DeliveryTrigger.AdminCancel, CanonicalDeliveryStatus.Cancelled)]
    public void Allowed_Edges_Resolve_To_Canonical_Destination(string from, string trigger, string expectedTo)
    {
        var result = DeliverySm.Validate(from, trigger);
        result.IsValid.Should().BeTrue($"'{from}' --{trigger}--> is in the frozen table");
        result.To.Should().Be(expectedTo);
        result.Error.Should().BeNull();
    }

    // ----- The two Ordered→Cancelled edges are distinguishable by trigger (D2) -

    [Fact]
    public void Two_Ordered_To_Cancelled_Edges_Are_Disambiguated_By_Trigger()
    {
        // Same (from, to) pair, different business reason — the whole point of
        // the trigger-keyed model (ADR-002 D2 / S13 penalty logic).
        DeliverySm.Validate(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.ClientCancelNoFee)
            .To.Should().Be(CanonicalDeliveryStatus.Cancelled);
        DeliverySm.Validate(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberCancelStrike)
            .To.Should().Be(CanonicalDeliveryStatus.Cancelled);

        // And they are genuinely different triggers, not aliases.
        DeliveryTrigger.ClientCancelNoFee.Should().NotBe(DeliveryTrigger.JeeberCancelStrike);
    }

    // ----- Off-table transitions reject with the typed 422 reason ------------

    [Theory]
    [InlineData(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.OtpVerified)]      // wrong trigger for Ordered
    [InlineData(CanonicalDeliveryStatus.InTransit, DeliveryTrigger.OtpVerified)]    // OTP only valid at AtDoor
    [InlineData(CanonicalDeliveryStatus.Picked, DeliveryTrigger.ClientCancelNoFee)] // no-fee client cancel only from Ordered
    public void Off_Table_Edge_Is_Rejected_As_TransitionNotAllowed(string from, string trigger)
    {
        var result = DeliverySm.Validate(from, trigger);
        result.IsValid.Should().BeFalse();
        result.Error!.Value.Reason.Should().Be(DeliverySm.ReasonTransitionNotAllowed);
        result.Error!.Value.From.Should().Be(from);
        result.Error!.Value.Trigger.Should().Be(trigger);
    }

    [Theory]
    [InlineData(CanonicalDeliveryStatus.Done)]
    [InlineData(CanonicalDeliveryStatus.Cancelled)]
    public void Transition_From_Terminal_State_Is_Rejected(string terminal)
    {
        var result = DeliverySm.Validate(terminal, DeliveryTrigger.JeeberTap);
        result.IsValid.Should().BeFalse();
        result.Error!.Value.Reason.Should().Be(DeliverySm.ReasonFromStateTerminalOrUnknown);
    }

    [Fact]
    public void Unknown_Trigger_Is_Rejected()
    {
        DeliverySm.Validate(CanonicalDeliveryStatus.Ordered, "not_a_real_trigger")
            .Error!.Value.Reason.Should().Be(DeliverySm.ReasonUnknownTrigger);
    }

    [Theory]
    [InlineData(null, DeliveryTrigger.JeeberTap)]
    [InlineData("", DeliveryTrigger.JeeberTap)]
    [InlineData(CanonicalDeliveryStatus.Ordered, null)]
    [InlineData(CanonicalDeliveryStatus.Ordered, "")]
    public void Null_Or_Blank_Inputs_Are_Rejected(string? from, string? trigger)
    {
        DeliverySm.Validate(from!, trigger!).IsValid.Should().BeFalse();
    }

    // ----- ValidateExplicit guards the destination ---------------------------

    [Fact]
    public void ValidateExplicit_Rejects_Destination_Mismatch()
    {
        DeliverySm.ValidateExplicit(
                CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.Done)
            .IsValid.Should().BeFalse("jeeber_tap from Ordered lands on Picked, not Done");

        DeliverySm.ValidateExplicit(
                CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.Picked)
            .IsValid.Should().BeTrue();
    }

    // ----- AllValidTransitions enumerates exactly 14 in-table rows -----------
    // (13 canonical edges + the AtDoor escalate_either alias = 14 table rows;
    //  the entry edge [*]→Ordered lives outside the table.)

    [Fact]
    public void AllValidTransitions_Enumerates_The_Fourteen_Table_Rows()
    {
        DeliverySm.AllValidTransitions().Should().HaveCount(14);
    }

    // ----- Trigger derivation (ADR-002 §3) -----------------------------------

    [Theory]
    [InlineData(CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.Picked, DeliveryTriggerSource.Jeeber, DeliveryTrigger.JeeberTap)]
    [InlineData(CanonicalDeliveryStatus.Picked, CanonicalDeliveryStatus.InTransit, DeliveryTriggerSource.Jeeber, DeliveryTrigger.JeeberTap)]
    [InlineData(CanonicalDeliveryStatus.InTransit, CanonicalDeliveryStatus.AtDoor, DeliveryTriggerSource.Jeeber, DeliveryTrigger.JeeberTap)]
    [InlineData(CanonicalDeliveryStatus.AtDoor, CanonicalDeliveryStatus.Done, DeliveryTriggerSource.System, DeliveryTrigger.OtpVerified)]
    [InlineData(CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.Cancelled, DeliveryTriggerSource.Client, DeliveryTrigger.ClientCancelNoFee)]
    [InlineData(CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.Cancelled, DeliveryTriggerSource.Jeeber, DeliveryTrigger.JeeberCancelStrike)]
    [InlineData(CanonicalDeliveryStatus.Picked, CanonicalDeliveryStatus.Cancelled, DeliveryTriggerSource.Jeeber, DeliveryTrigger.JeeberCancelHighStrike)]
    [InlineData(CanonicalDeliveryStatus.InTransit, CanonicalDeliveryStatus.FailedNeedsEscalation, DeliveryTriggerSource.Client, DeliveryTrigger.EscalateEither)]
    [InlineData(CanonicalDeliveryStatus.AtDoor, CanonicalDeliveryStatus.FailedNeedsEscalation, DeliveryTriggerSource.Jeeber, DeliveryTrigger.OtpFailOrJeeberEscalate)]
    [InlineData(CanonicalDeliveryStatus.FailedNeedsEscalation, CanonicalDeliveryStatus.Done, DeliveryTriggerSource.Admin, DeliveryTrigger.AdminResolve)]
    [InlineData(CanonicalDeliveryStatus.FailedNeedsEscalation, CanonicalDeliveryStatus.Cancelled, DeliveryTriggerSource.Admin, DeliveryTrigger.AdminCancel)]
    public void DeriveTrigger_Returns_The_Canonical_Trigger(string from, string to, string role, string expected)
    {
        DeliverySm.DeriveTrigger(from, to, role).Should().Be(expected);
    }

    [Fact]
    public void DeriveTrigger_Returns_Null_When_No_Canonical_Trigger_Fits()
    {
        // Backward / skip edges have no canonical trigger — the caller rejects
        // rather than guessing.
        DeliverySm.DeriveTrigger(CanonicalDeliveryStatus.Picked, CanonicalDeliveryStatus.Ordered, DeliveryTriggerSource.Jeeber)
            .Should().BeNull();
        DeliverySm.DeriveTrigger(CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.AtDoor, DeliveryTriggerSource.Jeeber)
            .Should().BeNull();
    }

    [Fact]
    public void Derived_Trigger_Then_Validate_Round_Trips_For_Happy_Path()
    {
        // The derivation must produce a trigger the table accepts and that
        // lands on the requested destination.
        var trigger = DeliverySm.DeriveTrigger(
            CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.Picked, DeliveryTriggerSource.Jeeber);
        trigger.Should().NotBeNull();
        var result = DeliverySm.ValidateExplicit(
            CanonicalDeliveryStatus.Ordered, trigger!, CanonicalDeliveryStatus.Picked);
        result.IsValid.Should().BeTrue();
    }
}
