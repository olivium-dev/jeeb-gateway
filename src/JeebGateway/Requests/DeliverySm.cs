namespace JeebGateway.Requests;

/// <summary>
/// The canonical SM-1 delivery state machine (ADR-002, owner-approved 2026-06-04).
///
/// This is the gateway-side, trigger-keyed <c>map[from][trigger] → to</c> freeze
/// of <c>delivery-service/internal/status/status.go::transitions</c>. It is the
/// only gateway-side delivery SM model: the legacy linear snake_case chain guard
/// was RETIRED in JEB-1479 (the gateway now forwards transitions to
/// delivery-service). New code should consult <see cref="DeliverySm"/>.
///
/// Why trigger-keyed (ADR-002 D2): two <c>Ordered → Cancelled</c> edges exist
/// with different penalty semantics (<c>client_cancel_no_fee</c> vs
/// <c>jeeber_cancel_strike</c>). A <c>(from,to)</c>-only model cannot tell them
/// apart; the table below can, because <c>to</c> is a function of
/// <c>(from, trigger)</c>, never of <c>(from, to)</c>.
///
/// This class is a pure function of the frozen table — no I/O, no DB, no clock —
/// exactly like its Go counterpart, so it is trivially unit-testable and the
/// parity test (<c>DeliverySmParityTests</c>, PR-4) can diff it against
/// <c>status.go</c>'s <c>AllValidTransitions()</c>.
/// </summary>
public static class DeliverySm
{
    /// <summary>
    /// The frozen <c>(from, trigger) → to</c> table. 13 explicit in-SM edges;
    /// the 14th transition (JEB-45 AC8) is the entry edge
    /// <c>[*] → Ordered</c> which lives in the offer/auction context and is
    /// intentionally NOT in this table (ADR-002 §2.3). Edge numbers in the
    /// comments match the ADR-002 §2.3 table and <c>status.go</c> verbatim.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Transitions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [CanonicalDeliveryStatus.Ordered] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeliveryTrigger.JeeberTap]          = CanonicalDeliveryStatus.Picked,                // 1
                [DeliveryTrigger.ClientCancelNoFee]  = CanonicalDeliveryStatus.Cancelled,             // 2
                [DeliveryTrigger.JeeberCancelStrike] = CanonicalDeliveryStatus.Cancelled,             // 3
                [DeliveryTrigger.EscalateEither]     = CanonicalDeliveryStatus.FailedNeedsEscalation, // 4
            },
            [CanonicalDeliveryStatus.Picked] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeliveryTrigger.JeeberTap]              = CanonicalDeliveryStatus.InTransit,             // 5
                [DeliveryTrigger.JeeberCancelHighStrike] = CanonicalDeliveryStatus.Cancelled,             // 6
                [DeliveryTrigger.EscalateEither]         = CanonicalDeliveryStatus.FailedNeedsEscalation, // 7
            },
            [CanonicalDeliveryStatus.InTransit] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeliveryTrigger.JeeberTap]      = CanonicalDeliveryStatus.AtDoor,                // 8
                [DeliveryTrigger.EscalateEither] = CanonicalDeliveryStatus.FailedNeedsEscalation, // 9
            },
            [CanonicalDeliveryStatus.AtDoor] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeliveryTrigger.OtpVerified]             = CanonicalDeliveryStatus.Done,                  // 10
                [DeliveryTrigger.OtpFailOrJeeberEscalate] = CanonicalDeliveryStatus.FailedNeedsEscalation, // 11
                // Alias of edge 11 so AC4 ("escalate from any non-terminal") is
                // uniform across all non-terminal states. A convenience duplicate
                // of an existing edge, NOT a 15th transition — frozen as-is to
                // match status.go's third AtDoor row exactly.
                [DeliveryTrigger.EscalateEither]          = CanonicalDeliveryStatus.FailedNeedsEscalation,
            },
            [CanonicalDeliveryStatus.FailedNeedsEscalation] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeliveryTrigger.AdminResolve] = CanonicalDeliveryStatus.Done,      // 12
                [DeliveryTrigger.AdminCancel]  = CanonicalDeliveryStatus.Cancelled, // 13
            },
            // Done and Cancelled are terminal — no outgoing rows (mirror of
            // status.go where they are absent from the transitions map).
        };

    /// <summary>
    /// Typed rejection reasons mirrored from <c>status.go</c> so the gateway's
    /// 422 body (<see cref="DeliveryTransitionError"/>) carries the SAME
    /// <c>reason</c> string the delivery-service would emit (defense in depth,
    /// ADR-002 §4). Public so the HTTP layer can echo them verbatim.
    /// </summary>
    public const string ReasonTransitionNotAllowed = "transition_not_allowed";
    public const string ReasonFromStateTerminalOrUnknown = "from_state_is_terminal_or_unknown";
    public const string ReasonUnknownTrigger = "unknown_trigger";

    /// <summary>
    /// Validates <c>(from, trigger)</c> against the frozen table and returns the
    /// destination state. The <see cref="DeliveryTransitionResultCanonical.IsValid"/>
    /// flag is false iff the edge is off-table; the controller maps that to
    /// HTTP 422 with <see cref="DeliveryTransitionResultCanonical.Error"/>
    /// (ADR-002 §4 — gateway fail-fast, no upstream round-trip needed).
    /// </summary>
    public static DeliveryTransitionResultCanonical Validate(string from, string trigger)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(trigger))
        {
            return DeliveryTransitionResultCanonical.Invalid(
                new DeliveryTransitionError(from ?? string.Empty, trigger ?? string.Empty, null,
                    ReasonTransitionNotAllowed));
        }

        if (!DeliveryTrigger.IsKnown(trigger))
        {
            return DeliveryTransitionResultCanonical.Invalid(
                new DeliveryTransitionError(from, trigger, null, ReasonUnknownTrigger));
        }

        if (!Transitions.TryGetValue(from, out var row))
        {
            // from is terminal (Done/Cancelled) or not a known state at all.
            return DeliveryTransitionResultCanonical.Invalid(
                new DeliveryTransitionError(from, trigger, null, ReasonFromStateTerminalOrUnknown));
        }

        if (!row.TryGetValue(trigger, out var to))
        {
            return DeliveryTransitionResultCanonical.Invalid(
                new DeliveryTransitionError(from, trigger, null, ReasonTransitionNotAllowed));
        }

        return DeliveryTransitionResultCanonical.Allowed(to);
    }

    /// <summary>
    /// Lower-level form that also asserts the caller's expected destination.
    /// Mirrors <c>status.go::ValidateExplicit</c> so a client that requested
    /// <c>to: Picked</c> from <c>Ordered</c> cannot silently land elsewhere if
    /// the table is later amended.
    /// </summary>
    public static DeliveryTransitionResultCanonical ValidateExplicit(string from, string trigger, string to)
    {
        var result = Validate(from, trigger);
        if (!result.IsValid)
        {
            return result;
        }
        if (!string.Equals(result.To, to, StringComparison.Ordinal))
        {
            return DeliveryTransitionResultCanonical.Invalid(
                new DeliveryTransitionError(from, trigger, to, ReasonTransitionNotAllowed));
        }
        return result;
    }

    /// <summary>
    /// Full enumeration of <c>(from, trigger, to)</c> triples in the table.
    /// Mirrors <c>status.go::AllValidTransitions()</c>. The parity test diffs
    /// this against the Go side; future migrations can diff against it too.
    /// </summary>
    public static IReadOnlyList<CanonicalTransition> AllValidTransitions()
    {
        var output = new List<CanonicalTransition>(16);
        foreach (var (from, row) in Transitions)
        {
            foreach (var (trigger, to) in row)
            {
                output.Add(new CanonicalTransition(from, trigger, to));
            }
        }
        return output;
    }

    /// <summary>
    /// Derives the canonical trigger for a transition when the caller has not
    /// (yet) sent an explicit <c>trigger</c> (ADR-002 §3 — "Trigger derivation
    /// for the gateway"). Mobile will start sending the trigger explicitly; until
    /// then the gateway infers it from <c>(from, to, callerRole)</c>. Returns
    /// null when no canonical trigger fits <c>(from, to)</c> — the caller then
    /// rejects the transition as off-table rather than guessing.
    ///
    /// This is the ONLY place the gateway encodes Jeeb business semantics on top
    /// of the generic table (the penalty-trigger choice), per the Golden-Rule-2
    /// split (ADR-002 §4): authority + trigger derivation are gateway-owned.
    /// </summary>
    public static string? DeriveTrigger(string from, string to, string callerRole)
    {
        // Forward happy-path edges are always jeeber_tap.
        if ((from == CanonicalDeliveryStatus.Ordered && to == CanonicalDeliveryStatus.Picked) ||
            (from == CanonicalDeliveryStatus.Picked && to == CanonicalDeliveryStatus.InTransit) ||
            (from == CanonicalDeliveryStatus.InTransit && to == CanonicalDeliveryStatus.AtDoor))
        {
            return DeliveryTrigger.JeeberTap;
        }

        // AtDoor → Done is only reachable via OTP verify (the gateway fires it
        // from the OTP-verify endpoint, never the bare PATCH).
        if (from == CanonicalDeliveryStatus.AtDoor && to == CanonicalDeliveryStatus.Done)
        {
            return DeliveryTrigger.OtpVerified;
        }

        // Cancellation edges: the penalty trigger is a function of who cancels
        // and from which state (S13).
        if (to == CanonicalDeliveryStatus.Cancelled)
        {
            if (from == CanonicalDeliveryStatus.Ordered)
            {
                return callerRole == DeliveryTriggerSource.Client
                    ? DeliveryTrigger.ClientCancelNoFee
                    : callerRole == DeliveryTriggerSource.Jeeber
                        ? DeliveryTrigger.JeeberCancelStrike
                        : callerRole == DeliveryTriggerSource.Admin
                            ? DeliveryTrigger.AdminCancel
                            : null;
            }
            if (from == CanonicalDeliveryStatus.Picked && callerRole == DeliveryTriggerSource.Jeeber)
            {
                return DeliveryTrigger.JeeberCancelHighStrike;
            }
            if (from == CanonicalDeliveryStatus.FailedNeedsEscalation && callerRole == DeliveryTriggerSource.Admin)
            {
                return DeliveryTrigger.AdminCancel;
            }
            return null;
        }

        // Escalation edges from any non-terminal → FailedNeedsEscalation.
        if (to == CanonicalDeliveryStatus.FailedNeedsEscalation)
        {
            // AtDoor escalations are modelled by the OTP-fail/Jeeber-escalate
            // trigger; status.go also accepts escalate_either there as an alias,
            // but we prefer the more specific token from AtDoor.
            if (from == CanonicalDeliveryStatus.AtDoor)
            {
                return DeliveryTrigger.OtpFailOrJeeberEscalate;
            }
            return DeliveryTrigger.EscalateEither;
        }

        // Admin resolve: FailedNeedsEscalation → Done.
        if (from == CanonicalDeliveryStatus.FailedNeedsEscalation &&
            to == CanonicalDeliveryStatus.Done &&
            callerRole == DeliveryTriggerSource.Admin)
        {
            return DeliveryTrigger.AdminResolve;
        }

        return null;
    }
}

/// <summary>
/// Row type for <see cref="DeliverySm.AllValidTransitions"/>. Mirrors
/// <c>status.go::Transition</c>.
/// </summary>
public readonly record struct CanonicalTransition(string From, string Trigger, string To);

/// <summary>
/// Result of a canonical transition validation. Carries the destination on
/// success and a typed <see cref="DeliveryTransitionError"/> on failure for the
/// 422 body.
/// </summary>
public readonly record struct DeliveryTransitionResultCanonical(bool IsValid, string? To, DeliveryTransitionError? Error)
{
    public static DeliveryTransitionResultCanonical Allowed(string to) => new(true, to, null);
    public static DeliveryTransitionResultCanonical Invalid(DeliveryTransitionError error) => new(false, null, error);
}

/// <summary>
/// Typed rejection payload for an off-table transition. Surfaced verbatim in the
/// gateway's 422 <c>transition_not_allowed</c> body (ADR-002 §4), matching the
/// shape delivery-service emits: <c>{ reason, from, trigger, to }</c>.
/// </summary>
public readonly record struct DeliveryTransitionError(string From, string Trigger, string? To, string Reason);
