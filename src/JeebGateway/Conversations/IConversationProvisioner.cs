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
}
