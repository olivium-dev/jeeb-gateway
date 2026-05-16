namespace JeebGateway.Requests;

/// <summary>
/// Strict state machine for the delivery lifecycle (T-backend-013, JEEB-31).
///
/// Valid forward transitions form a single linear chain:
///   pending → matched → accepted → picked_up → heading_off → delivered → rated
///
/// Anything else (skipping a step, going backwards, leaving a terminal
/// state) is rejected by <see cref="ValidateTransition"/>. The
/// PATCH /deliveries/{id}/status endpoint hands every request through
/// this guard before mutating the store.
///
/// Side-effects layered on top of the transition (NOT enforced here —
/// the controller owns them so the state machine stays a pure function):
///   * picked_up activates GPS-tracking on the row.
///   * delivered requires an OTP that the client supplies in the patch
///     body and that the store recorded on accept.
///   * every successful transition fans out a push to the "other party"
///     (Client → Jeeber and vice versa).
/// </summary>
public static class DeliveryStateMachine
{
    /// <summary>
    /// The single forward chain. Index <c>i</c> may only transition to
    /// index <c>i + 1</c>. Pre-acceptance states (<c>scheduled</c>) are
    /// not in this chain because they belong to the schedule-activation
    /// flow (T-backend-046) — they reach the machine through the
    /// activator's flip to <c>pending</c>.
    /// </summary>
    private static readonly string[] Chain =
    {
        RequestStatus.Pending,
        RequestStatus.Matched,
        RequestStatus.Accepted,
        RequestStatus.PickedUp,
        RequestStatus.HeadingOff,
        RequestStatus.Delivered,
        RequestStatus.Rated,
    };

    /// <summary>
    /// Returns the only status that <paramref name="from"/> may move to
    /// via this endpoint, or null when <paramref name="from"/> is the
    /// terminal end of the chain (<c>rated</c>) or otherwise outside the
    /// machine (cancelled, expired, disputed, scheduled).
    /// </summary>
    public static string? NextOf(string from)
    {
        for (var i = 0; i < Chain.Length - 1; i++)
        {
            if (string.Equals(Chain[i], from, StringComparison.Ordinal))
            {
                return Chain[i + 1];
            }
        }
        return null;
    }

    /// <summary>
    /// Validates a requested transition.
    /// <list type="bullet">
    ///   <item><see cref="TransitionValidation.Ok"/> — the transition is the
    ///     unique next step in the chain.</item>
    ///   <item><see cref="TransitionValidation.Invalid"/> — the request is
    ///     malformed (unknown status, skipping a step, going backwards,
    ///     same-state no-op, leaving a terminal state). Controller maps
    ///     to 400.</item>
    /// </list>
    /// </summary>
    public static TransitionValidation ValidateTransition(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return TransitionValidation.Invalid("from/to must be non-empty.");
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return TransitionValidation.Invalid($"already in '{from}'.");
        }

        var next = NextOf(from);
        if (next is null)
        {
            return TransitionValidation.Invalid(
                $"'{from}' has no forward transition (terminal or out-of-machine).");
        }

        if (!string.Equals(next, to, StringComparison.Ordinal))
        {
            return TransitionValidation.Invalid(
                $"invalid transition '{from}' → '{to}'. Only '{from}' → '{next}' is allowed.");
        }

        return TransitionValidation.Ok();
    }

    /// <summary>
    /// True for transitions that, once made, activate the GPS-tracking
    /// requirement (T-backend-013 AC: "picked_up activates GPS tracking").
    /// </summary>
    public static bool ActivatesGpsTracking(string from, string to)
        => string.Equals(to, RequestStatus.PickedUp, StringComparison.Ordinal);

    /// <summary>
    /// True for transitions that require the caller to present a valid
    /// OTP before the row can flip (T-backend-013 AC: "delivered requires
    /// OTP verification first"). The OTP itself is compared in the store.
    /// </summary>
    public static bool RequiresOtp(string from, string to)
        => string.Equals(to, RequestStatus.Delivered, StringComparison.Ordinal);
}

public readonly record struct TransitionValidation(bool IsValid, string? Reason)
{
    public static TransitionValidation Ok() => new(true, null);
    public static TransitionValidation Invalid(string reason) => new(false, reason);
}
