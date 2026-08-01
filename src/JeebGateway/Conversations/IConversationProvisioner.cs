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

    // S07 H6d — AdvanceToAcceptedAsync was DELETED here (owner ruling, 2026-08-01).
    //
    // It had exactly ONE production call site: OffersController's post-accept
    // orchestration on the retired POST /offers/{offerId}/accept route. That route
    // was a duplicate of POST /v1/offers/{id}/accept and is gone, so this member had
    // no caller left.
    //
    // It was also REDUNDANT while it existed. It advanced the legacy CHANNEL
    // aggregate (POST /api/members -> POST /api/channels/{id}/members, then PATCH
    // /api/members/{id}/deactivate for losers) to promote the winner and drop losers,
    // while the SAME caller, a few lines later, already did winner promotion + loser
    // removal ATOMICALLY on the correct aggregate via
    // IJeebConversationClient.AdvancePhaseAsync(WinnerUserId, RemoveOthers: true).
    // Its return value was discarded. Worse, its first step (POST /api/members) is NOT
    // channel-scoped, so it would have SUCCEEDED and minted an orphan chat member row
    // before the channel-scoped second step failed against the wrong aggregate — the
    // same Channels-vs-Conversations subsystem split documented on
    // CloseConversationAsync below.
    //
    // Do NOT reinstate it. Winner promotion and loser removal belong to the
    // conversation aggregate's phase-advance, which does both atomically.


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
    /// settled delivery completion into a 5xx (the same degrade-don't-fail contract the
    /// deleted AdvanceToAcceptedAsync carried). Idempotent: re-advancing an already-closed
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
