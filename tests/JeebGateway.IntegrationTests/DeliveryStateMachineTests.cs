using FluentAssertions;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-013 / JEEB-31 unit tests for <see cref="DeliveryStateMachine"/>.
///
/// Acceptance:
///   * Only the linear chain pending → matched → accepted → picked_up →
///     heading_off → delivered → rated is accepted.
///   * Every other transition (skip, backward, same-state, leaving a
///     terminal state, unknown status) is rejected.
///   * picked_up flips the GPS-tracking flag on commit.
///   * delivered requires OTP.
/// </summary>
public class DeliveryStateMachineTests
{
    // ----- Valid forward chain --------------------------------------------------

    [Theory]
    [InlineData(RequestStatus.Pending, RequestStatus.Matched)]
    [InlineData(RequestStatus.Matched, RequestStatus.Accepted)]
    [InlineData(RequestStatus.Accepted, RequestStatus.PickedUp)]
    [InlineData(RequestStatus.PickedUp, RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.HeadingOff, RequestStatus.Delivered)]
    [InlineData(RequestStatus.Delivered, RequestStatus.Rated)]
    public void Valid_Forward_Chain_Is_Allowed(string from, string to)
    {
        var result = DeliveryStateMachine.ValidateTransition(from, to);
        result.IsValid.Should().BeTrue($"'{from}' → '{to}' is the documented forward step");
    }

    // ----- Skips a step --------------------------------------------------------

    [Theory]
    [InlineData(RequestStatus.Pending, RequestStatus.Accepted)]
    [InlineData(RequestStatus.Pending, RequestStatus.Delivered)]
    [InlineData(RequestStatus.Matched, RequestStatus.PickedUp)]
    [InlineData(RequestStatus.Accepted, RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.Accepted, RequestStatus.Delivered)]
    [InlineData(RequestStatus.PickedUp, RequestStatus.Delivered)]
    [InlineData(RequestStatus.HeadingOff, RequestStatus.Rated)]
    public void Skipping_A_Step_Is_Rejected(string from, string to)
    {
        var result = DeliveryStateMachine.ValidateTransition(from, to);
        result.IsValid.Should().BeFalse($"'{from}' → '{to}' skips a step");
    }

    // ----- Backwards / regressions ---------------------------------------------

    [Theory]
    [InlineData(RequestStatus.Matched, RequestStatus.Pending)]
    [InlineData(RequestStatus.Accepted, RequestStatus.Matched)]
    [InlineData(RequestStatus.PickedUp, RequestStatus.Accepted)]
    [InlineData(RequestStatus.HeadingOff, RequestStatus.PickedUp)]
    [InlineData(RequestStatus.Delivered, RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.Rated, RequestStatus.Delivered)]
    public void Backward_Transitions_Are_Rejected(string from, string to)
    {
        var result = DeliveryStateMachine.ValidateTransition(from, to);
        result.IsValid.Should().BeFalse($"'{from}' → '{to}' is a backward transition");
    }

    // ----- Same-state no-ops ---------------------------------------------------

    [Theory]
    [InlineData(RequestStatus.Pending)]
    [InlineData(RequestStatus.Accepted)]
    [InlineData(RequestStatus.Delivered)]
    [InlineData(RequestStatus.Rated)]
    public void Same_State_Transition_Is_Rejected(string status)
    {
        var result = DeliveryStateMachine.ValidateTransition(status, status);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain($"already in '{status}'");
    }

    // ----- Leaving a terminal / out-of-machine state ----------------------------

    [Theory]
    [InlineData(RequestStatus.Rated, RequestStatus.Delivered)]
    [InlineData(RequestStatus.Cancelled, RequestStatus.Pending)]
    [InlineData(RequestStatus.Expired, RequestStatus.Pending)]
    [InlineData(RequestStatus.Disputed, RequestStatus.Pending)]
    [InlineData(RequestStatus.Scheduled, RequestStatus.Pending)]
    public void Transition_From_Terminal_Or_Out_Of_Machine_Is_Rejected(string from, string to)
    {
        var result = DeliveryStateMachine.ValidateTransition(from, to);
        result.IsValid.Should().BeFalse();
    }

    // ----- Malformed inputs ----------------------------------------------------

    [Fact]
    public void Unknown_From_Status_Is_Rejected()
    {
        DeliveryStateMachine.ValidateTransition("nope", RequestStatus.Matched)
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Unknown_To_Status_Is_Rejected()
    {
        DeliveryStateMachine.ValidateTransition(RequestStatus.Pending, "wat")
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, RequestStatus.Matched)]
    [InlineData("", RequestStatus.Matched)]
    [InlineData(RequestStatus.Pending, null)]
    [InlineData(RequestStatus.Pending, "")]
    [InlineData(RequestStatus.Pending, "   ")]
    public void Null_Or_Blank_Statuses_Are_Rejected(string? from, string? to)
    {
        DeliveryStateMachine.ValidateTransition(from!, to!)
            .IsValid.Should().BeFalse();
    }

    // ----- Side-effect markers -------------------------------------------------

    [Fact]
    public void PickedUp_Transition_Activates_Gps_Tracking()
    {
        DeliveryStateMachine.ActivatesGpsTracking(RequestStatus.Accepted, RequestStatus.PickedUp)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(RequestStatus.Pending, RequestStatus.Matched)]
    [InlineData(RequestStatus.Matched, RequestStatus.Accepted)]
    [InlineData(RequestStatus.PickedUp, RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.HeadingOff, RequestStatus.Delivered)]
    [InlineData(RequestStatus.Delivered, RequestStatus.Rated)]
    public void Other_Transitions_Do_Not_Activate_Gps_Tracking(string from, string to)
    {
        DeliveryStateMachine.ActivatesGpsTracking(from, to).Should().BeFalse();
    }

    [Fact]
    public void Delivered_Transition_Requires_Otp()
    {
        DeliveryStateMachine.RequiresOtp(RequestStatus.HeadingOff, RequestStatus.Delivered)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(RequestStatus.Pending, RequestStatus.Matched)]
    [InlineData(RequestStatus.Matched, RequestStatus.Accepted)]
    [InlineData(RequestStatus.Accepted, RequestStatus.PickedUp)]
    [InlineData(RequestStatus.PickedUp, RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.Delivered, RequestStatus.Rated)]
    public void Other_Transitions_Do_Not_Require_Otp(string from, string to)
    {
        DeliveryStateMachine.RequiresOtp(from, to).Should().BeFalse();
    }

    [Fact]
    public void NextOf_Returns_The_Single_Forward_Step()
    {
        DeliveryStateMachine.NextOf(RequestStatus.Pending).Should().Be(RequestStatus.Matched);
        DeliveryStateMachine.NextOf(RequestStatus.HeadingOff).Should().Be(RequestStatus.Delivered);
    }

    [Fact]
    public void NextOf_Returns_Null_At_End_Of_Chain()
    {
        DeliveryStateMachine.NextOf(RequestStatus.Rated).Should().BeNull();
    }

    [Theory]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Expired)]
    [InlineData(RequestStatus.Disputed)]
    [InlineData(RequestStatus.Scheduled)]
    public void NextOf_Returns_Null_For_Out_Of_Machine_States(string status)
    {
        DeliveryStateMachine.NextOf(status).Should().BeNull();
    }
}
