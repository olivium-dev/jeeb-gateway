namespace JeebGateway.Conversations;

/// <summary>
/// JEB-50 (S05 H7) feature flag for gateway-owned conversation auto-create on
/// order create. Independent from <c>FeatureFlags:DurableRequests</c> so the
/// broadcasting-conversation wire can be flipped on its own (staging-first) and
/// rolled back without touching the durable store cutover.
///
/// Default is <b>false</b> so today's green create path (S05 H3 → 201, with no
/// conversation id) is byte-for-byte unchanged until the chat-service
/// broadcasting summary (already shipped) is paired with this wire and
/// owner-approved. Flip via
/// <c>FeatureFlags__ConversationAutoCreate__Enabled=true</c> (a deploy
/// <c>workflow_dispatch</c> input).
/// </summary>
public sealed class ConversationProvisionOptions
{
    public const string SectionName = "FeatureFlags:ConversationAutoCreate";

    /// <summary>
    /// Master switch. Default <c>false</c> = today's path (no conversation
    /// auto-create; create DTO carries no <c>conversationId</c>). When true the
    /// gateway create orchestration auto-creates the broadcasting conversation
    /// and surfaces its id.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The <c>tag</c> sent to chat-service <c>POST /api/channels</c> for the
    /// order's broadcasting conversation. MUST match the marker chat-service's
    /// <c>ChannelSummaryService.ResolvePhase</c> reads to surface
    /// <c>phase: "broadcasting"</c> (the H9b assertion). Stable literal;
    /// overridable only for tests.
    /// </summary>
    public string BroadcastingTag { get; init; } = "broadcasting";
}
