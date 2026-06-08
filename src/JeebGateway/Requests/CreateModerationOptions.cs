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
/// Default is <b>false</b> so today's green path (S05 H3 → 201, plus A3/A5/N3)
/// is byte-for-byte unchanged until the lexicon is seeded and the gate is
/// flipped ON via a deploy <c>workflow_dispatch</c> input (owner-gated; this PR
/// does NOT flip it). The gate is purely orchestration: it composes the
/// existing gateway-owned <c>IProhibitedItemScanner</c> + ack ledger and the
/// lexicon stays gateway-owned (N11), so no gateway-side domain logic and no
/// ban-service coupling is introduced.
/// </summary>
public sealed class CreateModerationOptions
{
    public const string SectionName = "FeatureFlags:CreateModeration";

    /// <summary>
    /// Master switch. Default <c>false</c> = no create-time moderation gate
    /// (today's behaviour). Flip via
    /// <c>FeatureFlags__CreateModeration__Enabled=true</c> staging-first.
    /// </summary>
    public bool Enabled { get; init; }
}
