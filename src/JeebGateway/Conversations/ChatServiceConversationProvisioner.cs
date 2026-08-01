using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using JeebGateway.service.ServiceChat;

namespace JeebGateway.Conversations;

/// <summary>
/// JEB-50 (S05 H7 / H9b): the <see cref="IConversationProvisioner"/> backed by
/// chat-service's existing <c>POST /api/channels</c> via the NSwag-generated
/// <see cref="ServiceChatClient"/>. Pure thin orchestration — it composes one
/// existing typed client call, tags the channel <c>broadcasting</c> (so
/// chat-service's <c>ChannelSummaryService.ResolvePhase</c> surfaces
/// <c>phase: "broadcasting"</c>), and returns the channel id. It holds no
/// conversation state and no domain logic: chat-service owns the conversation.
///
/// LIFETIME: the durable create path runs inside a singleton
/// <c>DurableRequestsStore</c>, while <see cref="ServiceChatClient"/> is a
/// SCOPED typed client. This provisioner therefore opens a fresh DI scope per
/// call and resolves the chat client from it — so no scoped/HttpClient instance
/// is captured for the app lifetime (avoiding the captive-dependency pitfall)
/// and each order's conversation create gets a fresh pooled handler.
///
/// DEGRADE-DON'T-FAIL: a chat-service blip (timeout, 5xx, null id) returns
/// <c>null</c> and the order create still succeeds — the conversation is a
/// secondary side-effect of create, not the matching-resolve hard dependency.
/// This mirrors <c>StateServiceSagaBundleRecorder</c>'s contract.
/// </summary>
public sealed class ChatServiceConversationProvisioner : IConversationProvisioner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConversationProvisionOptions _options;
    private readonly ILogger<ChatServiceConversationProvisioner> _logger;

    public ChatServiceConversationProvisioner(
        IServiceScopeFactory scopeFactory,
        IOptions<ConversationProvisionOptions> options,
        ILogger<ChatServiceConversationProvisioner> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> CreateBroadcastingConversationAsync(
        string requestId,
        string clientId,
        CancellationToken ct)
    {
        if (!_options.Enabled) return null;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // SUBSYSTEM-ALIGNMENT FIX (2026-07-23): the previous implementation created
            // the order's conversation via chat-service's legacy CHANNELS subsystem
            // (POST /api/channels, ServiceChatClient) and returned the CHANNEL id. But
            // every message read/write goes through the CONVERSATIONS subsystem
            // (JeebConversationsController -> IJeebConversationClient -> POST/GET
            // /api/conversations/{id}/messages), which keys off a Conversation entity
            // (correlationKey == requestId) that a channel id does NOT resolve to — so
            // send/read 404'd and chat never worked end to end. Create the conversation
            // in the SAME subsystem the reads use: POST /api/conversations. chat-service
            // registers the client participant itself; no separate member-mint needed.
            var conversations = scope.ServiceProvider
                .GetRequiredService<JeebGateway.Conversations.Client.IJeebConversationClient>();

            var response = await conversations.CreateConversationAsync(
                new JeebGateway.Conversations.Client.CreateJeebConversationRequest
                {
                    RequestId = requestId,
                    ClientUserId = clientId,
                    OwnerRoleInConvo = "client",
                    Phase = "broadcasting",
                    IdempotencyKey = requestId,
                }, ct);

            var conversationId = response?.ConversationId;

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                _logger.LogWarning(
                    "Conversation auto-create for order {RequestId} returned no conversation id; order persists without a conversation.",
                    requestId);
                return null;
            }

            return conversationId;
        }
        catch (Exception ex)
        {
            // A chat-service outage must not fail the order create. Degrade:
            // the order persists with no conversation id (H9b unsatisfied for
            // this one order) rather than cascading a 500 onto POST /requests.
            _logger.LogWarning(ex,
                "Conversation auto-create for order {RequestId} unavailable; order persists without a conversation.",
                requestId);
            return null;
        }
    }

    // AdvanceToAcceptedAsync was DELETED here (owner ruling, 2026-08-01) together with
    // its interface declaration and its single caller, the retired
    // POST /offers/{offerId}/accept action on OffersController. See
    // IConversationProvisioner for why it was redundant as well as uncalled: it drove
    // the legacy CHANNEL aggregate while the same caller already promoted the winner
    // and removed losers atomically on the CONVERSATION aggregate.


    /// <inheritdoc />
    public async Task CloseConversationAsync(string? conversationId, CancellationToken ct)
    {
        // Auto-create disabled ⇒ this order never got a conversation to close.
        if (!_options.Enabled) return;

        // No broadcasting conversation was provisioned for this order (chat was down
        // at create, or ConversationAutoCreate was off then). Nothing to close.
        if (string.IsNullOrWhiteSpace(conversationId)) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // SUBSYSTEM-ALIGNMENT FIX (2026-08-01): this close previously targeted
            // chat-service's legacy CHANNELS subsystem
            // (PATCH /api/channels/{id}/deactivate, ServiceChatClient.DeactivateAsync).
            // That was correct only while create also minted a CHANNEL — and the
            // 2026-07-23 subsystem-alignment fix above moved create to
            // POST /api/conversations. The close was never moved with it, so it has
            // been handing a CONVERSATION id to the CHANNEL aggregate ever since:
            // chat-service looks the id up in the Firestore `Channels` collection,
            // misses, and Repository<T>.GetByIdAsync throws NoDataFoundException —
            // surfacing as an unhandled 500, retried 4x by the resilience pipeline,
            // and swallowed by the catch below. Net effect: EVERY completed delivery
            // left its conversation open at phase `accepted`, forever, silently.
            //
            // Drive the CONVERSATION aggregate instead — the same subsystem that
            // create/settle/messages already use. Still a pure composition of the
            // CONSUMED chat-service's own API: no gateway store write (GR-3), no
            // Firestore edit (GR-1), and NO chat-service change — `phase` is an
            // opaque caller-owned string upstream (AdvancePhaseAsync validates only
            // non-empty), so `closed` needs no new upstream capability. This retires
            // the round-3 (2026-07-07) "AdvancePhase closed vs DeactivateAsync"
            // disposition, whose premise — that `closed` was a chat-service capability
            // that did not exist yet — does not hold against the deployed service.
            //
            // Re-driving an already-closed conversation re-assigns the same phase and
            // is an upstream no-op, so a duplicate completion signal cannot corrupt
            // state (the idempotence the deactivate verb was chosen for is preserved).
            var conversations = scope.ServiceProvider
                .GetRequiredService<JeebGateway.Conversations.Client.IJeebConversationClient>();

            await conversations.AdvancePhaseAsync(
                conversationId,
                new JeebGateway.Conversations.Client.AdvanceJeebPhaseRequest
                {
                    Phase = _options.ClosedPhase,

                    // ROSTER MUST NOT MUTATE ON CLOSE. Both of these are deliberate
                    // overrides of the DTO's accept-shaped defaults (Phase="accepted",
                    // RemoveOthers=true): closing is a phase transition only. Left at
                    // the default, chat-service's else-branch would soft-remove every
                    // participant whose role maps to Restricted — the losing bidders on
                    // any conversation that completed without a prior settle — and a
                    // removed participant loses read access to the thread it is about
                    // to be asked to rate.
                    WinnerUserId = null,
                    RemoveOthers = false,
                }, ct);
        }
        catch (Exception ex)
        {
            // A chat-service outage must NEVER turn a committed, settled delivery
            // completion into a 5xx. Degrade: the conversation is left in its prior
            // state and the completion still returns 200 — mirrors the create/advance
            // contract. A reconcile/sweep is the backstop for a missed close.
            _logger.LogWarning(ex,
                "Delivery-complete close for conversation {ConversationId} unavailable; the completion stays committed, the conversation may close on retry/reconcile.",
                conversationId);
        }
    }
}
