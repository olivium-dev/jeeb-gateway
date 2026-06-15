using FluentAssertions;
using JeebGateway.Disputes.V2;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S14 / JEB-64 — pure-logic unit tests for the dispute state machine
/// (<see cref="DisputeCaseState"/>) and the in-store compare-and-set added in
/// PR #193. The three independent reviews called these out as the cheapest
/// high-value coverage in the PR: an exhaustive transition truth table, the
/// wire-contract projection (<see cref="DisputeCaseState.ToWire"/> /
/// <see cref="DisputeCaseState.DecisionForState"/>), the resolved_* → closed
/// terminal seal, and the concurrency guard that closes the TOCTOU window.
/// </summary>
public class DisputeCaseStateMachineTests
{
    // ----------------------------------------------------------------
    // CanTransition — exhaustive (from, to) truth table.
    // ----------------------------------------------------------------
    [Theory]
    // Legal moves
    [InlineData(DisputeCaseState.Open, DisputeCaseState.UnderReview, true)]
    [InlineData(DisputeCaseState.Open, DisputeCaseState.ResolvedRefund, true)]
    [InlineData(DisputeCaseState.Open, DisputeCaseState.ResolvedNoAction, true)]
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.ResolvedRefund, true)]
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.ResolvedNoAction, true)]
    [InlineData(DisputeCaseState.ResolvedRefund, DisputeCaseState.Closed, true)]
    [InlineData(DisputeCaseState.ResolvedNoAction, DisputeCaseState.Closed, true)]
    // Illegal: close before resolve (N6a)
    [InlineData(DisputeCaseState.Open, DisputeCaseState.Closed, false)]
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.Closed, false)]
    // Illegal: any exit from a resolved state other than the close seal
    [InlineData(DisputeCaseState.ResolvedRefund, DisputeCaseState.ResolvedNoAction, false)]
    [InlineData(DisputeCaseState.ResolvedNoAction, DisputeCaseState.ResolvedRefund, false)]
    [InlineData(DisputeCaseState.ResolvedRefund, DisputeCaseState.UnderReview, false)]
    [InlineData(DisputeCaseState.ResolvedRefund, DisputeCaseState.Open, false)]
    // Illegal: re-open / re-claim
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.Open, false)]
    // Illegal: closed is fully terminal — no exit at all
    [InlineData(DisputeCaseState.Closed, DisputeCaseState.Open, false)]
    [InlineData(DisputeCaseState.Closed, DisputeCaseState.UnderReview, false)]
    [InlineData(DisputeCaseState.Closed, DisputeCaseState.ResolvedRefund, false)]
    [InlineData(DisputeCaseState.Closed, DisputeCaseState.ResolvedNoAction, false)]
    // Illegal: self-loops
    [InlineData(DisputeCaseState.Open, DisputeCaseState.Open, false)]
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.UnderReview, false)]
    [InlineData(DisputeCaseState.Closed, DisputeCaseState.Closed, false)]
    public void CanTransition_Matches_TruthTable(string from, string to, bool expected)
    {
        DisputeCaseState.CanTransition(from, to).Should().Be(expected,
            "transition {0} → {1} legality must match the documented state machine", from, to);
    }

    [Fact]
    public void Closed_Is_Fully_Terminal_From_Every_Other_State()
    {
        // No state may transition OUT of closed, for any target.
        foreach (var to in DisputeCaseState.All)
        {
            DisputeCaseState.CanTransition(DisputeCaseState.Closed, to).Should().BeFalse(
                "closed → {0} must be illegal (closed is the terminal seal)", to);
        }
    }

    // ----------------------------------------------------------------
    // Wire contract — ToWire hyphenation + DecisionForState derivation.
    // ----------------------------------------------------------------
    [Theory]
    [InlineData(DisputeCaseState.Open, "open")]
    [InlineData(DisputeCaseState.UnderReview, "under_review")]
    [InlineData(DisputeCaseState.ResolvedRefund, "resolved-refund")]
    [InlineData(DisputeCaseState.ResolvedNoAction, "resolved-no-action")]
    [InlineData(DisputeCaseState.Closed, "closed")]
    public void ToWire_Hyphenates_Only_The_Resolved_States(string state, string expectedWire)
    {
        DisputeCaseState.ToWire(state).Should().Be(expectedWire);
    }

    [Theory]
    [InlineData(DisputeCaseState.Open, null)]
    [InlineData(DisputeCaseState.UnderReview, null)]
    [InlineData(DisputeCaseState.ResolvedRefund, "refund")]
    [InlineData(DisputeCaseState.ResolvedNoAction, "no-action")]
    [InlineData(DisputeCaseState.Closed, null)]
    public void DecisionForState_Derives_Verdict_From_Terminal_State(string state, string? expectedDecision)
    {
        DisputeCaseState.DecisionForState(state).Should().Be(expectedDecision);
    }

    [Fact]
    public void ToWire_Is_Presentation_Only_Never_A_Store_Token()
    {
        // The wire spelling must never collide with a canonical store token for
        // the resolved states (the doc warns ToWire output must not feed back
        // into CanTransition/the store). Underscore canonical ≠ hyphen wire.
        DisputeCaseState.ToWire(DisputeCaseState.ResolvedRefund).Should().NotBe(DisputeCaseState.ResolvedRefund);
        DisputeCaseState.All.Should().NotContain(DisputeCaseState.ToWire(DisputeCaseState.ResolvedNoAction));
    }

    // ----------------------------------------------------------------
    // In-store compare-and-set (TOCTOU fix) — the legality check is now
    // INSIDE the store lock, so an illegal review/close returns
    // InvalidTransition (mapped to 409) instead of clobbering, and
    // NotFound stays distinct from InvalidTransition.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Store_ApplyReview_On_NonOpen_Case_Returns_InvalidTransition_Not_Clobber()
    {
        var store = new InMemoryDisputeCaseStore();
        var c = await store.AddAsync(NewCase("case_rev", DisputeCaseState.UnderReview), CancellationToken.None);
        var firstReviewer = c.ReviewedByAdminId;

        var result = await store.ApplyReviewAsync(c.Id, "second-admin", DateTimeOffset.UtcNow, CancellationToken.None);

        result.Outcome.Should().Be(DisputeMutationOutcome.InvalidTransition,
            "the store re-checks open → under_review under the lock; an already-under_review case is not re-claimable");
        // The conflicting row was NOT mutated (no last-writer-wins).
        result.Case!.ReviewedByAdminId.Should().Be(firstReviewer);
    }

    [Fact]
    public async Task Store_ApplyClose_On_Open_Case_Returns_InvalidTransition()
    {
        var store = new InMemoryDisputeCaseStore();
        var c = await store.AddAsync(NewCase("case_close_open", DisputeCaseState.Open), CancellationToken.None);

        var result = await store.ApplyCloseAsync(c.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        result.Outcome.Should().Be(DisputeMutationOutcome.InvalidTransition);
        result.Case!.State.Should().Be(DisputeCaseState.Open, "an illegal close must not advance the state");
    }

    [Fact]
    public async Task Store_ApplyReview_Unknown_Id_Returns_NotFound_Distinct_From_InvalidTransition()
    {
        var store = new InMemoryDisputeCaseStore();
        var result = await store.ApplyReviewAsync("case_missing", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        result.Outcome.Should().Be(DisputeMutationOutcome.NotFound);
        result.Case.Should().BeNull("NotFound must never be conflated with InvalidTransition (404 vs 409)");
    }

    [Fact]
    public async Task Store_ApplyReview_Legal_Open_Case_Updates_And_Stamps_Reviewer()
    {
        var store = new InMemoryDisputeCaseStore();
        var c = await store.AddAsync(NewCase("case_ok", DisputeCaseState.Open), CancellationToken.None);
        var at = DateTimeOffset.UtcNow;

        var result = await store.ApplyReviewAsync(c.Id, "admin-ok", at, CancellationToken.None);

        result.Outcome.Should().Be(DisputeMutationOutcome.Updated);
        result.Case!.State.Should().Be(DisputeCaseState.UnderReview);
        result.Case.ReviewedByAdminId.Should().Be("admin-ok");
        result.Case.ReviewedAt.Should().Be(at);
    }

    [Fact]
    public async Task Store_Concurrent_Reviews_On_Same_Open_Case_Only_One_Wins()
    {
        // The whole point of the compare-and-set: even if N callers all read the
        // case as `open` and race ApplyReviewAsync, exactly ONE applies the
        // transition; the rest get InvalidTransition (no last-writer-wins on
        // ReviewedAt/admin). Drive 32 concurrent claims at one open case.
        var store = new InMemoryDisputeCaseStore();
        await store.AddAsync(NewCase("case_race", DisputeCaseState.Open), CancellationToken.None);

        var tasks = Enumerable.Range(0, 32).Select(i =>
            store.ApplyReviewAsync("case_race", $"admin-{i}", DateTimeOffset.UtcNow, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Outcome == DisputeMutationOutcome.Updated).Should().Be(1,
            "exactly one concurrent review may win the open → under_review claim");
        results.Count(r => r.Outcome == DisputeMutationOutcome.InvalidTransition).Should().Be(31,
            "every other concurrent claim must lose the compare-and-set with InvalidTransition, never a silent clobber");
    }

    [Fact]
    public async Task Store_Concurrent_Closes_On_Same_Resolved_Case_Only_One_Wins()
    {
        var store = new InMemoryDisputeCaseStore();
        await store.AddAsync(NewCase("case_close_race", DisputeCaseState.ResolvedRefund), CancellationToken.None);

        var tasks = Enumerable.Range(0, 16).Select(_ =>
            store.ApplyCloseAsync("case_close_race", DateTimeOffset.UtcNow, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Outcome == DisputeMutationOutcome.Updated).Should().Be(1,
            "exactly one concurrent close may seal a resolved case");
        results.Count(r => r.Outcome == DisputeMutationOutcome.InvalidTransition).Should().Be(15);
    }

    private static DisputeCase NewCase(string id, string state) => new()
    {
        Id = id,
        DeliveryId = $"d_{id}",
        OpenedByUserId = "opener",
        Reason = "test",
        State = state,
        OpenedAt = DateTimeOffset.UtcNow,
        // Pre-stamp a reviewer when seeding an under_review row so the
        // "not clobbered" assertion has a value to compare against.
        ReviewedByAdminId = state == DisputeCaseState.UnderReview ? "first-admin" : null,
    };
}
