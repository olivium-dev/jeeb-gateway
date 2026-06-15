namespace JeebGateway.Disputes.V2;

/// <summary>
/// T-BE-028 / JEB-64 dispute-case lifecycle.
///
/// Distinct from <see cref="JeebGateway.Disputes.DisputeState"/> (the
/// legacy T-backend-025 / JEEB-43 surface) so the two flows can coexist
/// without breaking the existing /deliveries/{id}/dispute API. The v2
/// case surface backs the new <c>POST /v1/deliveries/{id}/escalate</c>
/// + <c>POST /admin/v1/disputes/{id}/resolve</c> endpoints.
///
/// Valid transitions:
/// <code>
///   open → under_review                   (admin queues for triage)
///   open | under_review → resolved_refund     (admin closes WITH refund)
///   open | under_review → resolved_no_action  (admin closes without action)
///   resolved_refund | resolved_no_action → closed (terminal seal)
/// </code>
///
/// <c>open</c>, <c>under_review</c> and the two <c>resolved_*</c> states
/// are addressable from the admin queue. <c>closed</c> is purely a
/// post-resolution archival flag and is set automatically when the
/// resolution lands — the API does not expose a separate "close" verb.
/// </summary>
public static class DisputeCaseState
{
    public const string Open = "open";
    public const string UnderReview = "under_review";
    public const string ResolvedRefund = "resolved_refund";
    public const string ResolvedNoAction = "resolved_no_action";
    public const string Closed = "closed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Open,
        UnderReview,
        ResolvedRefund,
        ResolvedNoAction,
        Closed
    };

    /// <summary>
    /// A case in any of these states is considered resolved — the admin
    /// queue must not re-open them and the second resolve call returns
    /// 409 already_resolved (AC3).
    /// </summary>
    public static readonly IReadOnlySet<string> ResolvedStates = new HashSet<string>(StringComparer.Ordinal)
    {
        ResolvedRefund,
        ResolvedNoAction,
        Closed
    };

    public static bool IsResolved(string state) => ResolvedStates.Contains(state);

    /// <summary>
    /// T-BE-028 wire contract: the two resolved states serialize hyphenated
    /// (<c>resolved-refund</c> / <c>resolved-no-action</c>) on the API
    /// surface, while the internal enum + state machine + state-service
    /// stay underscore-cased. <c>open</c>, <c>under_review</c> and
    /// <c>closed</c> are unchanged. Presentation-only — never feed a wire
    /// value back into <see cref="CanTransition"/> or the store.
    /// </summary>
    public static string ToWire(string state) => state switch
    {
        ResolvedRefund => "resolved-refund",
        ResolvedNoAction => "resolved-no-action",
        _ => state
    };

    /// <summary>
    /// JEB-64 wire contract: the admin verdict implied by a terminal
    /// state. <c>resolved_refund → refund</c>, <c>resolved_no_action →
    /// no-action</c>; null while still open / under_review (no verdict
    /// yet). <c>closed</c> retains whatever resolution preceded the seal,
    /// so a closed case has no standalone decision projection here.
    /// </summary>
    public static string? DecisionForState(string state) => state switch
    {
        ResolvedRefund => "refund",
        ResolvedNoAction => "no-action",
        _ => null
    };

    /// <summary>
    /// A case in a terminal-resolved state (<c>resolved_refund</c> /
    /// <c>resolved_no_action</c>) can still be sealed via the close
    /// transition; <c>closed</c> itself is fully terminal.
    /// </summary>
    private static bool IsCloseable(string state) =>
        string.Equals(state, ResolvedRefund, StringComparison.Ordinal)
        || string.Equals(state, ResolvedNoAction, StringComparison.Ordinal);

    public static bool CanTransition(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return false;

        // resolved_* → closed is the only legal transition out of a
        // resolved state (the terminal seal). All other moves out of a
        // resolved state are illegal (already_resolved / invalid-transition).
        if (string.Equals(to, Closed, StringComparison.Ordinal))
        {
            return IsCloseable(from);
        }

        if (IsResolved(from)) return false;

        return to switch
        {
            UnderReview => string.Equals(from, Open, StringComparison.Ordinal),
            ResolvedRefund =>
                string.Equals(from, Open, StringComparison.Ordinal)
                || string.Equals(from, UnderReview, StringComparison.Ordinal),
            ResolvedNoAction =>
                string.Equals(from, Open, StringComparison.Ordinal)
                || string.Equals(from, UnderReview, StringComparison.Ordinal),
            _ => false
        };
    }
}
