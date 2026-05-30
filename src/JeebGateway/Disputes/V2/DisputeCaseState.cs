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

    public static bool CanTransition(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return false;
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
