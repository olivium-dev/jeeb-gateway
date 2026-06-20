using FluentAssertions;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// WS-04 (SM-1 hardening) — the FUL-01 Jeeber-milestone chain, the FUL-02
/// customer stepper, terminal-state guards, lateral recovery, and the FUL-05
/// back-out semantics, all driven as SEQUENCED walks of the pure
/// <see cref="DeliverySm"/> table (no I/O, mirrors status.go).
///
/// The existing <c>DeliverySmTests</c> assert each edge in isolation. These
/// assert the table composes into the full lifecycle the scenarios require —
/// a green delivery walks Ordered→Picked→InTransit→AtDoor→Done end to end, a
/// Done/Cancelled row is absorbing, and an escalated row is admin-recoverable.
/// </summary>
public class DeliverySmLifecycleTests
{
    /// <summary>
    /// Walks a sequence of (trigger, expectedTo) steps from a start state,
    /// asserting every hop is legal and lands where SM-1 says it must. Returns
    /// the terminal state reached.
    /// </summary>
    private static string WalkChain(string start, params (string trigger, string expectedTo)[] steps)
    {
        var state = start;
        foreach (var (trigger, expectedTo) in steps)
        {
            var result = DeliverySm.Validate(state, trigger);
            result.IsValid.Should().BeTrue($"'{state}' --{trigger}--> must be a legal SM-1 edge");
            result.To.Should().Be(expectedTo, $"'{state}' --{trigger}--> must land on {expectedTo}");
            state = result.To!;
        }
        return state;
    }

    // ----- FUL-01 / FUL-02: the full happy-path lifecycle composes ----------

    [Fact]
    public void Full_Happy_Path_Walks_Ordered_To_Done()
    {
        // The Jeeber-milestone chain (FUL-01) and the customer stepper (FUL-02)
        // are the same SM-1 spine: Ordered→Picked→InTransit→AtDoor→Done.
        var terminal = WalkChain(CanonicalDeliveryStatus.Ordered,
            (DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.Picked),     // confirm heading off / mark picked up
            (DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.InTransit),  // heading to drop off
            (DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.AtDoor),     // arrived at door
            (DeliveryTrigger.OtpVerified, CanonicalDeliveryStatus.Done));    // OTP-verified handover

        terminal.Should().Be(CanonicalDeliveryStatus.Done);
        CanonicalDeliveryStatus.IsTerminal(terminal).Should().BeTrue();
    }

    [Fact]
    public void Happy_Path_Cannot_Skip_A_Step()
    {
        // FUL-02 stepper is strictly ordered: you cannot jump Ordered→InTransit
        // or Ordered→AtDoor. jeeber_tap from Ordered only reaches Picked.
        DeliverySm.Validate(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberTap)
            .To.Should().Be(CanonicalDeliveryStatus.Picked);
        // There is no trigger that takes Ordered straight to AtDoor or Done.
        DeliverySm.Validate(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.OtpVerified)
            .IsValid.Should().BeFalse("OTP handover is only legal at AtDoor");
    }

    // ----- Terminal-state guards (Done / Cancelled are absorbing) -----------

    [Theory]
    [InlineData(CanonicalDeliveryStatus.Done)]
    [InlineData(CanonicalDeliveryStatus.Cancelled)]
    public void No_Trigger_Escapes_A_Terminal_State(string terminal)
    {
        // Every trigger in the lexicon must be rejected from a terminal state —
        // a delivered or cancelled order can never re-open (FUL-05 reassign must
        // therefore reuse a NON-terminal order id, never resurrect a Done one).
        foreach (var trigger in DeliveryTrigger.All)
        {
            var result = DeliverySm.Validate(terminal, trigger);
            result.IsValid.Should().BeFalse($"'{terminal}' is absorbing; '{trigger}' must be rejected");
            result.Error!.Value.Reason.Should().Be(DeliverySm.ReasonFromStateTerminalOrUnknown);
        }
    }

    // ----- Lateral recovery: escalate from any non-terminal, then resolve ----

    [Theory]
    [InlineData(CanonicalDeliveryStatus.Ordered)]
    [InlineData(CanonicalDeliveryStatus.Picked)]
    [InlineData(CanonicalDeliveryStatus.InTransit)]
    public void Escalate_Is_Legal_From_Every_Mid_Flight_State(string from)
    {
        // AC4: escalate_either is uniform across non-terminal mid-flight states.
        DeliverySm.Validate(from, DeliveryTrigger.EscalateEither)
            .To.Should().Be(CanonicalDeliveryStatus.FailedNeedsEscalation);
    }

    [Fact]
    public void Escalated_Order_Is_Admin_Recoverable_To_Done()
    {
        // FailedNeedsEscalation is deliberately NON-terminal: admin resolves it
        // forward to Done or laterally to Cancelled. A mid-flight escalation
        // (e.g. from InTransit) recovers without a dead end.
        var escalated = WalkChain(CanonicalDeliveryStatus.InTransit,
            (DeliveryTrigger.EscalateEither, CanonicalDeliveryStatus.FailedNeedsEscalation),
            (DeliveryTrigger.AdminResolve, CanonicalDeliveryStatus.Done));
        escalated.Should().Be(CanonicalDeliveryStatus.Done);
    }

    [Fact]
    public void Escalated_Order_Is_Admin_Cancellable()
    {
        var cancelled = WalkChain(CanonicalDeliveryStatus.AtDoor,
            (DeliveryTrigger.OtpFailOrJeeberEscalate, CanonicalDeliveryStatus.FailedNeedsEscalation),
            (DeliveryTrigger.AdminCancel, CanonicalDeliveryStatus.Cancelled));
        cancelled.Should().Be(CanonicalDeliveryStatus.Cancelled);
    }

    // ----- FUL-05 back-out: cancellation edges by who backs out --------------

    [Fact]
    public void Jeeber_Backout_Pre_Pickup_Is_A_Strike_Cancel()
    {
        // FUL-05: a Jeeber backing out before pickup (from Ordered) cancels with
        // the strike penalty; the customer then reassigns/re-broadcasts (out of
        // SM scope — same order id stays Cancelled here, a new offer drives a new
        // delivery row, never a resurrection of this one).
        DeliverySm.Validate(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberCancelStrike)
            .To.Should().Be(CanonicalDeliveryStatus.Cancelled);
    }

    [Fact]
    public void Jeeber_Backout_After_Pickup_Is_A_High_Strike_Cancel()
    {
        // Backing out AFTER pickup (from Picked) carries the heavier penalty.
        DeliverySm.Validate(CanonicalDeliveryStatus.Picked, DeliveryTrigger.JeeberCancelHighStrike)
            .To.Should().Be(CanonicalDeliveryStatus.Cancelled);
    }

    [Fact]
    public void Jeeber_Cannot_HighStrike_Cancel_Before_Pickup()
    {
        // The high-strike cancel is only legal from Picked (post-pickup). From
        // Ordered it is off-table — the pre-pickup cancel is the strike variant.
        DeliverySm.Validate(CanonicalDeliveryStatus.Ordered, DeliveryTrigger.JeeberCancelHighStrike)
            .IsValid.Should().BeFalse();
    }
}

/// <summary>
/// WS-04 — lifecycle walks expressed in the LEGACY vocabulary, proving the
/// ADR-002 §3 alias dual-read layer composes with the canonical SM so an
/// in-flight order persisted in old tokens (picked_up / heading_off / at_door)
/// still validates its next hop. This is the "do not 422 a running delivery
/// mid-flight" guarantee (D4) under sequencing.
/// </summary>
public class DeliverySmAliasLifecycleTests
{
    /// <summary>Resolve a (possibly legacy) status to canonical, then validate the trigger.</summary>
    private static DeliveryTransitionResultCanonical ValidateAlias(string persistedStatus, string trigger)
    {
        var canonical = DeliveryStatusAlias.ToCanonical(persistedStatus);
        canonical.Should().NotBeNull($"'{persistedStatus}' must resolve to a canonical state");
        return DeliverySm.Validate(canonical!, trigger);
    }

    [Theory]
    // A row persisted as legacy 'accepted' is canonically Ordered → jeeber_tap → Picked.
    [InlineData(RequestStatus.Accepted, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.Picked)]
    // 'picked_up' (Picked) → jeeber_tap → InTransit.
    [InlineData(RequestStatus.PickedUp, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.InTransit)]
    // 'heading_off' (InTransit) → jeeber_tap → AtDoor.
    [InlineData(RequestStatus.HeadingOff, DeliveryTrigger.JeeberTap, CanonicalDeliveryStatus.AtDoor)]
    // 'at_door' (AtDoor) → otp_verified → Done.
    [InlineData(RequestStatus.AtDoor, DeliveryTrigger.OtpVerified, CanonicalDeliveryStatus.Done)]
    public void InFlight_Legacy_Row_Validates_Its_Next_Canonical_Hop(string persisted, string trigger, string expectedTo)
    {
        var result = ValidateAlias(persisted, trigger);
        result.IsValid.Should().BeTrue($"a row at legacy '{persisted}' must still advance under SM-1");
        result.To.Should().Be(expectedTo);
    }

    [Theory]
    [InlineData(RequestStatus.Delivered)]  // delivered ⇒ Done (terminal)
    [InlineData(RequestStatus.Cancelled)]  // cancelled ⇒ Cancelled (terminal)
    public void InFlight_Legacy_Terminal_Row_Rejects_Further_Transitions(string persistedTerminal)
    {
        // A legacy 'delivered'/'cancelled' row resolves to a canonical terminal
        // state and must reject any further trigger — the terminal guard holds
        // across the alias layer too.
        ValidateAlias(persistedTerminal, DeliveryTrigger.JeeberTap)
            .IsValid.Should().BeFalse();
    }
}
