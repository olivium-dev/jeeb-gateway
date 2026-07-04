using Microsoft.Extensions.Options;

namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// T-backend-024 (JEEB-42) — see <see cref="ICancellationService"/>.
/// JEB-1507: thresholds are now configurable via <see cref="CancellationPolicyOptions"/>
/// (appsettings <c>CancellationPolicy</c> section) instead of hardcoded constants.
/// </summary>
public sealed class CancellationService : ICancellationService
{
    /// <summary>
    /// Fallback constants kept for tests that reference the old names.
    /// Production code reads from the injected <see cref="CancellationPolicyOptions"/>.
    /// </summary>
    internal const int JeeberCancellationThreshold = 3;
    internal static readonly TimeSpan JeeberCancellationWindow = TimeSpan.FromDays(7);
    internal static readonly TimeSpan JeeberRestrictionDuration = TimeSpan.FromHours(24);

    // ---- PR-G2: canonical-vocab cancellation phase sets ---------------------
    //
    // The system is mid-migration between the legacy gateway vocabulary
    // (accepted/picked_up/heading_off/at_door) and the canonical SM-1 vocabulary
    // (Ordered/Picked/InTransit/AtDoor). A persisted row can carry EITHER form, so
    // membership is computed on the CANONICAL form (DeliveryStatusAlias.ToCanonical
    // first) rather than string-matching one vocabulary. Before this, a row persisted
    // as canonical 'Picked' was absent from the legacy {picked_up, heading_off} sets
    // and a legitimate post-pickup / jeeber cancel 409'd.
    //
    // The canonical phase sets below are the SOURCE OF TRUTH for the phase gate; the
    // store re-checks membership against the RAW persisted status under its write
    // lock, so the sets forwarded to IRequestsStore.TryCancelAsync are EXPANDED to
    // also contain every legacy alias that resolves into the canonical set (built once
    // in ExpandForStore). Canonical membership and the expanded raw membership are
    // therefore equivalent for every known token.

    /// <summary>
    /// Canonical states a Client may cancel from WITHOUT admin approval — everything
    /// strictly before pickup. <see cref="CanonicalDeliveryStatus.Ordered"/> is the
    /// canonical entry edge (legacy <c>accepted</c>). The pre-acceptance request
    /// tokens <c>scheduled/pending/matched</c> live in the request lifecycle and have
    /// NO canonical delivery status, so they are carried separately in
    /// <see cref="ClientPrePickupPreAcceptLegacy"/>.
    /// </summary>
    internal static readonly IReadOnlySet<string> ClientPrePickupCanonical =
        new HashSet<string>(StringComparer.Ordinal)
        {
            CanonicalDeliveryStatus.Ordered,
        };

    /// <summary>
    /// Pre-acceptance request-lifecycle tokens (no canonical delivery status) a Client
    /// may still cancel freely: a future-dated <c>scheduled</c> row and the open-auction
    /// <c>pending/matched</c> rows, before any jeeber is bound.
    /// </summary>
    internal static readonly IReadOnlySet<string> ClientPrePickupPreAcceptLegacy =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.Scheduled,
            RequestStatus.Pending,
            RequestStatus.Matched,
        };

    /// <summary>Canonical states a Client may cancel from AFTER pickup — these
    /// require admin sign-off (the row parks on <c>cancellation_requested</c>).</summary>
    internal static readonly IReadOnlySet<string> ClientPostPickupCanonical =
        new HashSet<string>(StringComparer.Ordinal)
        {
            CanonicalDeliveryStatus.Picked,
            CanonicalDeliveryStatus.InTransit,
        };

    /// <summary>Canonical states a Jeeber may cancel from — every state the Jeeber is
    /// bound to and on the hook for, from the <see cref="CanonicalDeliveryStatus.Ordered"/>
    /// entry edge through the <see cref="CanonicalDeliveryStatus.AtDoor"/> handover step.
    /// (The legacy set was missing <c>at_door</c> entirely — a jeeber at the door could
    /// not cancel.)</summary>
    internal static readonly IReadOnlySet<string> JeeberCancellableCanonical =
        new HashSet<string>(StringComparer.Ordinal)
        {
            CanonicalDeliveryStatus.Ordered,
            CanonicalDeliveryStatus.Picked,
            CanonicalDeliveryStatus.InTransit,
            CanonicalDeliveryStatus.AtDoor,
        };

    /// <summary>
    /// Every legacy gateway status token that carries a canonical delivery mapping.
    /// Used to expand a canonical phase set into the raw-vocabulary set the store's
    /// under-lock re-check compares against.
    /// </summary>
    private static readonly IReadOnlyList<string> LegacyDeliveryTokens = new[]
    {
        RequestStatus.Accepted,
        RequestStatus.PickedUp,
        RequestStatus.HeadingOff,
        RequestStatus.AtDoor,
    };

    /// <summary>
    /// Builds the raw-vocabulary from-state set forwarded to
    /// <see cref="IRequestsStore.TryCancelAsync"/>: the canonical tokens themselves,
    /// plus every legacy alias resolving into <paramref name="canonicalSet"/>, plus
    /// (for the client pre-pickup phase) the pre-acceptance legacy tokens. This keeps
    /// the store's raw <c>allowedFromStates.Contains(row.Status)</c> re-check equivalent
    /// to the service's canonical membership gate for a row persisted under EITHER vocab.
    /// </summary>
    private static IReadOnlySet<string> ExpandForStore(
        IReadOnlySet<string> canonicalSet, bool includePreAcceptLegacy)
    {
        var set = new HashSet<string>(canonicalSet, StringComparer.Ordinal);
        foreach (var legacy in LegacyDeliveryTokens)
        {
            var canonical = DeliveryStatusAlias.ToCanonical(legacy);
            if (canonical is not null && canonicalSet.Contains(canonical))
            {
                set.Add(legacy);
            }
        }
        if (includePreAcceptLegacy)
        {
            foreach (var token in ClientPrePickupPreAcceptLegacy)
            {
                set.Add(token);
            }
        }
        return set;
    }

    /// <summary>Raw-vocabulary from-states for the client pre-pickup immediate-cancel path.</summary>
    private static readonly IReadOnlySet<string> ClientPrePickupFromStates =
        ExpandForStore(ClientPrePickupCanonical, includePreAcceptLegacy: true);

    /// <summary>Raw-vocabulary from-states for the client post-pickup admin-queue path.</summary>
    private static readonly IReadOnlySet<string> ClientPostPickupFromStates =
        ExpandForStore(ClientPostPickupCanonical, includePreAcceptLegacy: false);

    /// <summary>Raw-vocabulary from-states for the jeeber unilateral-cancel path.</summary>
    private static readonly IReadOnlySet<string> JeeberCancellableFromStates =
        ExpandForStore(JeeberCancellableCanonical, includePreAcceptLegacy: false);

    /// <summary>
    /// Canonical membership gate: resolves <paramref name="status"/> to its canonical
    /// form and tests it against <paramref name="canonicalSet"/>. When the token has no
    /// canonical delivery mapping (pre-acceptance) it optionally falls back to the
    /// pre-acceptance legacy set.
    /// </summary>
    private static bool InPhase(
        string status, IReadOnlySet<string> canonicalSet, bool allowPreAcceptLegacy)
    {
        var canonical = DeliveryStatusAlias.ToCanonical(status);
        if (canonical is not null && canonicalSet.Contains(canonical))
        {
            return true;
        }
        return allowPreAcceptLegacy && ClientPrePickupPreAcceptLegacy.Contains(status);
    }

    private readonly IRequestsStore _store;
    private readonly IJeeberRestrictionStore _restrictions;
    private readonly TimeProvider _clock;
    private readonly CancellationPolicyOptions _policy;

    public CancellationService(
        IRequestsStore store,
        IJeeberRestrictionStore restrictions,
        TimeProvider clock,
        IOptions<CancellationPolicyOptions> policy)
    {
        _store = store;
        _restrictions = restrictions;
        _clock = clock;
        _policy = policy.Value;
    }

    public async Task<CancellationResult> CancelAsync(
        string deliveryId,
        string callerUserId,
        bool callerIsClient,
        bool callerIsJeeber,
        string? reason,
        CancellationToken ct)
    {
        var row = await _store.GetAsync(deliveryId, ct);
        if (row is null)
        {
            return new CancellationResult(
                CancellationOutcome.NotFound, null, null, null, false, null, null);
        }

        var actingAsClient = callerIsClient
            && string.Equals(row.ClientId, callerUserId, StringComparison.Ordinal);
        var actingAsJeeber = callerIsJeeber
            && row.JeeberId is { } jid
            && string.Equals(jid, callerUserId, StringComparison.Ordinal);

        if (!actingAsClient && !actingAsJeeber)
        {
            return new CancellationResult(
                CancellationOutcome.NotAuthorized, row, row.Status, null, false, null, null);
        }

        var trimmedReason = reason?.Trim();
        var now = _clock.GetUtcNow();

        // Jeeber path — mandatory reason, then 3+/7d threshold check.
        // Note: a Jeeber who is also the Client (dual-role on the same id)
        // can only happen if they accepted their own request, which BR-1
        // forbids. Treat the Jeeber branch first when the row's JeeberId
        // matches the caller — the post-pickup-Client branch would also
        // match for a self-accepted row and we'd misroute the cancel.
        if (actingAsJeeber)
        {
            if (string.IsNullOrWhiteSpace(trimmedReason))
            {
                return new CancellationResult(
                    CancellationOutcome.ReasonRequired, row, row.Status, null, false, null, null);
            }

            var storeResult = await _store.TryCancelAsync(
                deliveryId,
                JeeberCancellableFromStates,
                RequestStatus.Cancelled,
                cancelledBy: "jeeber",
                reason: trimmedReason,
                at: now,
                ct);

            if (storeResult is null)
            {
                return new CancellationResult(
                    CancellationOutcome.NotFound, null, null, null, false, null, null);
            }
            if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
            {
                return new CancellationResult(
                    CancellationOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus,
                    null, false, null, null);
            }

            // WeeklyThreshold+/rolling-window threshold. Counted AFTER the
            // commit so the cancel we just landed counts toward the trigger —
            // without that, the Nth cancel in the window would only ever trip
            // the (N+1)th time.
            var jeeberId = storeResult.Request.JeeberId!;
            var rolling = await GetJeeberCancellationCountLast7DaysAsync(jeeberId, now, ct);

            var triggered = false;
            DateTimeOffset? expiresAt = null;
            var restrictionDuration = TimeSpan.FromHours(_policy.RestrictionDurationHours);
            if (rolling >= _policy.WeeklyThreshold)
            {
                await _restrictions.ApplyAsync(jeeberId, now, restrictionDuration, ct);
                expiresAt = now + restrictionDuration;
                triggered = true;
            }

            return new CancellationResult(
                CancellationOutcome.CancelledByJeeber,
                storeResult.Request,
                storeResult.PreviousStatus,
                trimmedReason,
                triggered,
                expiresAt,
                rolling);
        }

        // Client path — pre-pickup is free, post-pickup goes to admin queue.
        if (actingAsClient)
        {
            if (InPhase(row.Status, ClientPrePickupCanonical, allowPreAcceptLegacy: true))
            {
                var storeResult = await _store.TryCancelAsync(
                    deliveryId,
                    ClientPrePickupFromStates,
                    RequestStatus.Cancelled,
                    cancelledBy: "client",
                    reason: trimmedReason,
                    at: now,
                    ct);

                if (storeResult is null)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotFound, null, null, null, false, null, null);
                }
                if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus,
                        null, false, null, null);
                }

                return new CancellationResult(
                    CancellationOutcome.CancelledImmediately,
                    storeResult.Request,
                    storeResult.PreviousStatus,
                    trimmedReason,
                    false,
                    null,
                    null);
            }

            if (InPhase(row.Status, ClientPostPickupCanonical, allowPreAcceptLegacy: false))
            {
                var storeResult = await _store.TryCancelAsync(
                    deliveryId,
                    ClientPostPickupFromStates,
                    RequestStatus.CancellationRequested,
                    cancelledBy: "client",
                    reason: trimmedReason,
                    at: now,
                    ct);

                if (storeResult is null)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotFound, null, null, null, false, null, null);
                }
                if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus,
                        null, false, null, null);
                }

                return new CancellationResult(
                    CancellationOutcome.PendingAdminApproval,
                    storeResult.Request,
                    storeResult.PreviousStatus,
                    trimmedReason,
                    false,
                    null,
                    null);
            }

            // Any other state — terminal or admin-pending — is uncancellable
            // by the Client.
            return new CancellationResult(
                CancellationOutcome.NotCancellable, row, row.Status, null, false, null, null);
        }

        // Defensive: unreachable, both flags must be false here.
        return new CancellationResult(
            CancellationOutcome.NotAuthorized, row, row.Status, null, false, null, null);
    }

    public async Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingApprovalsAsync(
        int page, int pageSize, CancellationToken ct)
    {
        return await _store.ListPendingCancellationsAsync(page, pageSize, ct);
    }

    public async Task<AdminCancellationDecisionResult> DecideAsync(
        string deliveryId, string action, CancellationToken ct)
    {
        bool approve;
        if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
        {
            approve = true;
        }
        else if (string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase))
        {
            approve = false;
        }
        else
        {
            return new AdminCancellationDecisionResult(
                AdminCancellationDecisionOutcome.UnknownAction, null, null);
        }

        var result = await _store.TryDecideCancellationAsync(deliveryId, approve, _clock.GetUtcNow(), ct);
        if (result is null)
        {
            // Either not found or the row is no longer in cancellation_requested.
            // Distinguish for the controller: a fresh GetAsync tells us which.
            var existing = await _store.GetAsync(deliveryId, ct);
            if (existing is null)
            {
                return new AdminCancellationDecisionResult(
                    AdminCancellationDecisionOutcome.NotFound, null, null);
            }
            return new AdminCancellationDecisionResult(
                AdminCancellationDecisionOutcome.NotPending, existing, existing.Status);
        }

        return new AdminCancellationDecisionResult(
            approve ? AdminCancellationDecisionOutcome.Approved : AdminCancellationDecisionOutcome.Rejected,
            result.Request,
            result.PreviousStatus);
    }

    public async Task<int> GetJeeberCancellationCountAsync(string jeeberId, CancellationToken ct)
    {
        var rows = await _store.ListJeeberCancelledAsync(jeeberId, ct);
        return rows.Count;
    }

    public async Task<int> GetJeeberCancellationCountLast7DaysAsync(
        string jeeberId, DateTimeOffset at, CancellationToken ct)
    {
        var window = TimeSpan.FromDays(_policy.WeeklyWindowDays);
        var since = at - window;
        var rows = await _store.ListJeeberCancelledAsync(jeeberId, ct);
        var count = 0;
        foreach (var r in rows)
        {
            if (r.CancellationRequestedAt is { } when_ && when_ >= since && when_ <= at)
            {
                count++;
            }
        }
        return count;
    }
}
