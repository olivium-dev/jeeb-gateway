using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// WS-04 (SM-1 hardening). Locks the contract of the flag-gated typed-422 body
/// builder <see cref="CanonicalTransitionProblem"/> — the ADR-002 §4
/// <c>transition_not_allowed</c> shape that <c>CanonicalTransition422</c>
/// will swap in during its canary window.
///
/// Before this file the builder had ZERO exercising tests: it was referenced
/// only by doc-comments. The point of WS-04 is to "solidify the in-memory SM-1
/// AND the typed body so the flip is a no-op behavior swap later" — that
/// requires the 422 envelope to be pinned: the four <c>{ reason, from, trigger,
/// to }</c> extension fields, the <c>application/problem+json</c> content type,
/// the 422 status, alias→canonical normalization of from/to inside the body,
/// and gateway-side trigger derivation when the caller did not send an explicit
/// trigger. If any of these drift, the canary flip would silently change the
/// wire contract — exactly what the canary discipline forbids.
/// </summary>
public class CanonicalTransitionProblemTests
{
    private static ProblemDetails Body(ObjectResult result)
    {
        result.StatusCode.Should().Be(CanonicalTransitionProblem.Status422UnprocessableEntity);
        result.ContentTypes.Should().Contain("application/problem+json",
            "RFC 7807 transition errors must negotiate application/problem+json");
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(CanonicalTransitionProblem.Status422UnprocessableEntity);
        return problem;
    }

    // ----- The 422 envelope carries the four typed extension fields ----------

    [Fact]
    public void Build_Emits_The_Four_Typed_Extension_Fields()
    {
        // Picked --(client_cancel_no_fee)--> is off-table (no-fee cancel is only
        // legal from Ordered) — a representative illegal edge with an explicit
        // trigger so the body must echo it verbatim.
        var result = CanonicalTransitionProblem.Build(
            from: CanonicalDeliveryStatus.Picked,
            to: CanonicalDeliveryStatus.Cancelled,
            trigger: DeliveryTrigger.ClientCancelNoFee,
            callerRole: DeliveryTriggerSource.Client,
            detail: "no-fee client cancel is only legal from Ordered");

        var problem = Body(result);
        problem.Type.Should().Be("https://jeeb.dev/errors/transition-not-allowed");
        problem.Extensions["reason"].Should().Be(DeliverySm.ReasonTransitionNotAllowed);
        problem.Extensions["from"].Should().Be(CanonicalDeliveryStatus.Picked);
        problem.Extensions["to"].Should().Be(CanonicalDeliveryStatus.Cancelled);
        problem.Extensions["trigger"].Should().Be(DeliveryTrigger.ClientCancelNoFee);
        problem.Detail.Should().Be("no-fee client cancel is only legal from Ordered");
    }

    // ----- Legacy from/to aliases are normalized to canonical in the body ----

    [Theory]
    [InlineData(RequestStatus.PickedUp, CanonicalDeliveryStatus.Picked)]
    [InlineData(RequestStatus.HeadingOff, CanonicalDeliveryStatus.InTransit)]
    [InlineData(RequestStatus.Delivered, CanonicalDeliveryStatus.Done)]
    [InlineData(RequestStatus.AtDoor, CanonicalDeliveryStatus.AtDoor)]
    [InlineData(RequestStatus.Disputed, CanonicalDeliveryStatus.FailedNeedsEscalation)]
    public void Build_Normalizes_Legacy_From_Alias_To_Canonical(string legacyFrom, string canonicalFrom)
    {
        // A persisted in-flight row may still carry a legacy token (ADR-002 §3
        // dual-read). The 422 body must speak the canonical lexicon regardless,
        // so the canary flip never leaks snake_case onto the wire.
        var result = CanonicalTransitionProblem.Build(
            from: legacyFrom,
            to: RequestStatus.Delivered,
            trigger: null,
            callerRole: DeliveryTriggerSource.Jeeber,
            detail: null);

        var problem = Body(result);
        problem.Extensions["from"].Should().Be(canonicalFrom);
        problem.Extensions["to"].Should().Be(CanonicalDeliveryStatus.Done);
    }

    // ----- Trigger is DERIVED when the caller omits it -----------------------

    [Fact]
    public void Build_Derives_Trigger_When_Not_Supplied_Happy_Edge()
    {
        // Ordered → Picked with no explicit trigger: the builder must derive
        // jeeber_tap (ADR-002 §3) so the body still names the business reason.
        var result = CanonicalTransitionProblem.Build(
            from: CanonicalDeliveryStatus.Ordered,
            to: CanonicalDeliveryStatus.Picked,
            trigger: null,
            callerRole: DeliveryTriggerSource.Jeeber,
            detail: null);

        Body(result).Extensions["trigger"].Should().Be(DeliveryTrigger.JeeberTap);
    }

    [Fact]
    public void Build_Derives_Penalty_Trigger_From_Caller_Role()
    {
        // Same Ordered→Cancelled (from,to) pair, different caller role ⇒
        // different derived penalty trigger. This is the S13 distinction the
        // trigger-keyed model exists to preserve; the 422 body must reflect it.
        var clientCancel = CanonicalTransitionProblem.Build(
            CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.Cancelled,
            trigger: null, callerRole: DeliveryTriggerSource.Client, detail: null);
        Body(clientCancel).Extensions["trigger"].Should().Be(DeliveryTrigger.ClientCancelNoFee);

        var jeeberCancel = CanonicalTransitionProblem.Build(
            CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.Cancelled,
            trigger: null, callerRole: DeliveryTriggerSource.Jeeber, detail: null);
        Body(jeeberCancel).Extensions["trigger"].Should().Be(DeliveryTrigger.JeeberCancelStrike);
    }

    [Fact]
    public void Build_Trigger_Is_Null_When_Underivable()
    {
        // A backward / skip edge has no canonical trigger — the body carries a
        // null trigger rather than guessing (the caller already rejected it).
        var result = CanonicalTransitionProblem.Build(
            from: CanonicalDeliveryStatus.Picked,
            to: CanonicalDeliveryStatus.Ordered,
            trigger: null,
            callerRole: DeliveryTriggerSource.Jeeber,
            detail: null);

        Body(result).Extensions["trigger"].Should().BeNull();
    }

    [Fact]
    public void Build_Defaults_Caller_Role_To_System_When_Absent()
    {
        // No caller role supplied: derivation falls back to system. A happy
        // forward edge is still jeeber_tap (role-independent); a role-keyed
        // penalty edge becomes underivable (null), never throws.
        var forward = CanonicalTransitionProblem.Build(
            CanonicalDeliveryStatus.InTransit, CanonicalDeliveryStatus.AtDoor,
            trigger: null, callerRole: null, detail: null);
        Body(forward).Extensions["trigger"].Should().Be(DeliveryTrigger.JeeberTap);

        var penalty = CanonicalTransitionProblem.Build(
            CanonicalDeliveryStatus.Ordered, CanonicalDeliveryStatus.Cancelled,
            trigger: null, callerRole: null, detail: null);
        Body(penalty).Extensions["trigger"].Should().BeNull();
    }
}

/// <summary>
/// WS-04 guardrail regression: the contract-affecting, canary-gated
/// <see cref="UpstreamFeatureFlags.CanonicalTransition422"/> flag MUST default
/// OFF on a freshly-bound options object. WS-04 explicitly must NOT flip it
/// live; this test fails the build if a future edit defaults it ON, which would
/// silently swap the 400→422 wire contract outside the canary window.
/// </summary>
public class CanonicalTransition422FlagDefaultTests
{
    [Fact]
    public void CanonicalTransition422_Defaults_Off()
    {
        new UpstreamFeatureFlags().CanonicalTransition422
            .Should().BeFalse("the 422 contract swap is canary-gated and must never default on (WS-04 guardrail)");
    }
}
