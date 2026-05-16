namespace JeebGateway.Disputes;

/// <summary>
/// Dispute lifecycle states (T-backend-025 / JEEB-43).
///
/// Valid transitions:
///   filed → under_review        (admin opens the case)
///   under_review → resolved     (admin closes in favor of the filer / partial)
///   under_review → dismissed    (admin closes without action)
///
/// <c>resolved</c> and <c>dismissed</c> are terminal — once landed, the row
/// cannot transition further. <c>filed</c> may also go directly to a
/// terminal state when an admin's first action is to dismiss/resolve the
/// case without an explicit "open" step.
/// </summary>
public static class DisputeState
{
    public const string Filed = "filed";
    public const string UnderReview = "under_review";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Filed,
        UnderReview,
        Resolved,
        Dismissed
    };

    public static readonly IReadOnlySet<string> TerminalStates = new HashSet<string>(StringComparer.Ordinal)
    {
        Resolved,
        Dismissed
    };

    public static bool IsTerminal(string state) => TerminalStates.Contains(state);

    /// <summary>
    /// True when <paramref name="from"/> can move to <paramref name="to"/>
    /// under admin authority. Used by <see cref="IDisputeService"/> to
    /// reject stale or impossible transitions.
    /// </summary>
    public static bool CanTransition(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return false;
        if (IsTerminal(from)) return false;

        return to switch
        {
            UnderReview => string.Equals(from, Filed, StringComparison.Ordinal),
            Resolved => string.Equals(from, Filed, StringComparison.Ordinal)
                        || string.Equals(from, UnderReview, StringComparison.Ordinal),
            Dismissed => string.Equals(from, Filed, StringComparison.Ordinal)
                         || string.Equals(from, UnderReview, StringComparison.Ordinal),
            _ => false
        };
    }
}
