namespace JeebGateway.Conversations;

/// <summary>
/// JEB-50 (S05 H7 / H9b): auto-creates the broadcasting conversation that backs
/// an order, returning its id so the gateway can surface it to the client as
/// <c>conversationId</c>.
///
/// OWNING MECHANISM (decision): the conversation is auto-created by the GATEWAY
/// on order-create, NOT by chat-api self-triggering on an order-created event.
/// Microservices may not call each other and there is no event bus, so chat-api
/// cannot observe an order; the order-create path lives in the gateway BFF, so
/// the gateway is the only place that can compose "order created → broadcasting
/// conversation created". chat-api already owns the conversation itself: it
/// persists the channel (with its <c>tag</c>/<c>type</c> markers) and derives
/// the read-only <c>phase</c> ("broadcasting") from those markers on
/// <c>GET /api/channels/{id}/summary</c>. This provisioner is pure thin
/// orchestration over chat-api's existing <c>POST /api/channels</c> typed client
/// — it holds NO conversation state and NO domain logic of its own.
///
/// RESILIENCE: a chat-service blip must NEVER fail the order create. The
/// implementation degrades to <c>null</c> (the order persists without a
/// conversation id; H9b stays unsatisfied for that one order but the create is
/// still 201) rather than throwing — mirroring the saga-bundle recorder's
/// degrade-don't-fail contract.
/// </summary>
public interface IConversationProvisioner
{
    /// <summary>
    /// Auto-creates the broadcasting conversation for a freshly created order
    /// and returns its id. Returns <c>null</c> when conversation auto-create is
    /// disabled by configuration, or when the chat-service was unavailable /
    /// returned no usable id — in every null case the caller leaves the order's
    /// <c>ConversationId</c> unset and the create still succeeds.
    /// </summary>
    /// <param name="requestId">The order/request id (used only for the channel name + logging correlation).</param>
    /// <param name="clientId">The ordering client's id — recorded as the channel's initiating member.</param>
    Task<string?> CreateBroadcastingConversationAsync(
        string requestId,
        string clientId,
        CancellationToken ct);

    /// <summary>
    /// S07 H6d (fix C): advances an already-provisioned broadcasting conversation
    /// to the <c>accepted</c> phase when the request owner accepts a jeeber's
    /// offer. ORCHESTRATION ONLY — the gateway is the SOLE chat caller (org no-
    /// coupling law; offer-service holds no chat client). On a successful accept
    /// the gateway:
    /// <list type="number">
    ///   <item>mints/adds the <b>winning</b> jeeber as a member of the channel
    ///     (POST /api/members → POST /api/channels/{id}/members) so the accepted
    ///     conversation has the client + winning jeeber as active participants;</item>
    ///   <item>tags the channel <c>accepted</c> so chat-service's
    ///     <c>ChannelSummaryService.ResolvePhase</c> can surface
    ///     <c>phase: "accepted"</c> once it recognises the marker (the recognise-
    ///     "accepted" half is a chat-service change tracked separately — see the
    ///     implementation remarks);</item>
    ///   <item>best-effort deactivates each supplied losing chat member id
    ///     (PATCH /api/members/{id}/deactivate) so the auction losers drop out
    ///     while history is retained.</item>
    /// </list>
    ///
    /// DEGRADE-DON'T-FAIL: a chat-service blip (timeout, 5xx, null id) is logged
    /// and swallowed — a chat outage must NEVER turn a successful offer accept
    /// into a 5xx. Returns the winning jeeber's minted chat member id on success,
    /// or <c>null</c> when auto-create is disabled / there is no conversation id /
    /// the chat call degraded. Mirrors
    /// <see cref="CreateBroadcastingConversationAsync"/>'s contract.
    /// </summary>
    /// <param name="conversationId">
    /// The channel id minted at create time and stamped on the request
    /// (<c>DeliveryRequest.ConversationId</c>). When null/empty the method is a
    /// no-op (the order never got a broadcasting conversation).
    /// </param>
    /// <param name="winningJeeberId">
    /// The user-management id of the jeeber whose offer was accepted. Recorded on
    /// the new chat member's name for correlation.
    /// </param>
    /// <param name="losingMemberIds">
    /// Chat member ids of the losing offerers to deactivate. May be empty when the
    /// gateway has not retained the losers' chat member ids (see remarks) — the
    /// winner-add + accepted-tag still run.
    /// </param>
    Task<string?> AdvanceToAcceptedAsync(
        string? conversationId,
        string winningJeeberId,
        IReadOnlyList<string> losingMemberIds,
        CancellationToken ct);

    /// <summary>
    /// E22 / I3 (JEBV4-241, cross-ref JEBV4-217; Q-036): auto-close the conversation
    /// that backs a delivery once that delivery <b>completes</b>. The close is routed
    /// through the <b>consumed</b> chat-service's OWN API — the Lane-I consumption path —
    /// via the conversation phase-advance verb it already exposes
    /// (<c>PATCH /api/conversations/{id}/phase</c>,
    /// <c>IJeebConversationClient.AdvancePhaseAsync</c>). It is NOT a gateway store write
    /// (GR-3) and NOT a Firestore direct edit (GR-1); the gateway holds no conversation
    /// state and only composes one existing typed-client call.
    ///
    /// <para>MECHANISM RECONCILIATION — the round-3 (2026-07-07) disposition is RETIRED
    /// (2026-08-01). Round 3 chose the legacy CHANNEL-deactivate verb
    /// (<c>PATCH /api/channels/{id}/deactivate</c>) over the conversation phase-advance,
    /// on the premise that <c>closed</c> "is a chat-service capability that does NOT exist
    /// yet" and would need the extension protocol + owner approval. <b>Both halves of that
    /// premise are false against the deployed service, and the chosen alternative never
    /// worked.</b>
    /// <list type="number">
    ///   <item><c>phase</c> is an OPAQUE, caller-owned string upstream:
    ///     <c>ConversationService.AdvancePhaseAsync</c> validates only that it is
    ///     non-empty and then assigns it, and chat-service documents the whole
    ///     vocabulary as caller-owned ("stores and compares but never enumerates by
    ///     product meaning"). <c>closed</c> therefore needs ZERO chat-service change and
    ///     trips no approval gate.</item>
    ///   <item>The deactivate verb targets the legacy CHANNEL aggregate, a DIFFERENT
    ///     Firestore collection (<c>Channels</c>) from the one create/settle/messages
    ///     use (<c>Conversations</c>). Since the 2026-07-23 subsystem-alignment fix moved
    ///     create to <c>POST /api/conversations</c>, the id handed to deactivate has been
    ///     a conversation id the channel aggregate cannot resolve — so every close 500'd
    ///     (unhandled <c>NoDataFoundException</c>), was retried 4x, and was swallowed by
    ///     the degrade-don't-fail catch. Observed live 2026-08-01 on conversation
    ///     <c>158efb52-30f6-4eb6-ae4e-ccab859e481f</c>, which still reads
    ///     <c>phase: "accepted"</c> long after its delivery reached Done.</item>
    /// </list>
    /// The conversation phase-advance is therefore the ONE writer for the agent-scoped
    /// close: same subsystem as every other conversation call, no chat-service change.</para>
    ///
    /// <para>DEGRADE-DON'T-FAIL: a chat blip / disabled auto-create flag / null-or-empty
    /// conversation id is a silent no-op — a chat outage must NEVER turn a committed,
    /// settled delivery completion into a 5xx (mirrors
    /// <see cref="AdvanceToAcceptedAsync"/>). Idempotent: re-advancing an already-closed
    /// conversation re-assigns the same phase and is a no-op upstream. Default no-op
    /// implementation so existing <see cref="IConversationProvisioner"/> fakes need no
    /// change (additive-first).</para>
    /// </summary>
    /// <param name="conversationId">
    /// The CONVERSATION id minted at create time (<c>POST /api/conversations</c>) and
    /// stamped on the delivery row (<c>DeliveryRequest.ConversationId</c>). When
    /// null/empty the method is a no-op (the order never got a broadcasting
    /// conversation).
    /// </param>
    Task CloseConversationAsync(string? conversationId, CancellationToken ct)
        => Task.CompletedTask;
}
