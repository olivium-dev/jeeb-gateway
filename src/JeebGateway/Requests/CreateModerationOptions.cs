namespace JeebGateway.Requests;

/// <summary>
/// JEB-63 / S05: feature flag for the gateway-owned create-time prohibited-items
/// moderation gate on <c>POST /requests</c>. When <see cref="Enabled"/> is true
/// the create path runs the gateway's prohibited-items scanner BEFORE persisting
/// the request and:
///   <list type="bullet">
///     <item>hard-rejects a <c>block</c>-severity match with 409
///       <c>prohibited_item_blocked</c> (an ack must NOT override it — AC1/AC7);</item>
///     <item>soft-rejects a <c>warn</c>-severity match with 409
///       <c>prohibited_item_requires_ack</c> until the caller has acknowledged
///       the current lexicon version, then lets the create through (AC3).</item>
///   </list>
///
/// Default is now <b>true</b> (S05 round-2): prohibited-items screening is a
/// safety control, so it is ON by default and — critically — DECOUPLED from
/// <c>FeatureFlags:DurableRequests</c>. The two flags are independent: the
/// moderation gate runs (and the lexicon is seeded) regardless of whether the
/// durable saga create path is active, so "arak"/"knife" are rejected with 409
/// (N1/A1.1) whether durable_requests is ON or OFF. To disable the gate (e.g. a
/// staging soak with a not-yet-curated lexicon) set
/// <c>FeatureFlags__CreateModeration__Enabled=false</c> explicitly; absence of
/// the key means ON. The gate is purely orchestration: it composes the existing
/// gateway-owned <c>IProhibitedItemScanner</c> + ack ledger and the lexicon
/// stays gateway-owned (N11), so no gateway-side domain logic and no
/// ban-service coupling is introduced.
/// </summary>
public sealed class CreateModerationOptions
{
    public const string SectionName = "FeatureFlags:CreateModeration";

    /// <summary>
    /// Master switch for create-time prohibited-items screening. Default
    /// <c>true</c> (ON, independent of the durable-requests flag). Set
    /// <c>FeatureFlags__CreateModeration__Enabled=false</c> to turn the gate
    /// off explicitly.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
