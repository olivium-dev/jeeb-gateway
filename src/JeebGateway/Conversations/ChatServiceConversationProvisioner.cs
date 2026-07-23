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

    /// <inheritdoc />
    public async Task<string?> AdvanceToAcceptedAsync(
        string? conversationId,
        string winningJeeberId,
        IReadOnlyList<string> losingMemberIds,
        CancellationToken ct)
    {
        if (!_options.Enabled) return null;

        // No broadcasting conversation was provisioned for this order (chat was
        // down at create, or ConversationAutoCreate was off then). Nothing to
        // advance — degrade silently. The accept itself is unaffected.
        if (string.IsNullOrWhiteSpace(conversationId)) return null;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var chat = scope.ServiceProvider.GetRequiredService<ServiceChatClient>();

            // (1) Mint the WINNING jeeber as a chat member. chat-service mints the
            //     member id (MemberService.CreateAsync → response.ID); the client
            //     cannot supply it — same constraint as the client member at
            //     create. Carry the user-management jeeber id on Name/Nickname for
            //     correlation. Tag the member 'accepted' so a future chat-service
            //     ResolvePhase can attribute the winner role.
            var memberResponse = await chat.MembersPOST2Async(new CreateMemberRequest
            {
                Name = winningJeeberId,
                Nickname = winningJeeberId,
                Type = "jeeber",
                Tag = _options.AcceptedTag,
            }, ct);

            var winnerMemberId = memberResponse?.Id;
            if (string.IsNullOrWhiteSpace(winnerMemberId))
            {
                _logger.LogWarning(
                    "Accept-advance for conversation {ConversationId} could not register a chat member for winning jeeber {JeeberId}; conversation left in broadcasting phase.",
                    conversationId, winningJeeberId);
                return null;
            }

            // (2) Add the minted winning-jeeber member to the channel so the
            //     accepted conversation has client + winning jeeber as active
            //     participants (POST /api/channels/{id}/members). Best-effort:
            //     if chat rejects the add we still attempted loser-removal below.
            try
            {
                await chat.MembersPOSTAsync(conversationId, new AddChannelMembersRequest
                {
                    ChannelId = conversationId,
                    MemberId = winnerMemberId,
                }, ct);
            }
            catch (Exception addEx)
            {
                _logger.LogWarning(addEx,
                    "Accept-advance: adding winning jeeber member {MemberId} to channel {ConversationId} failed; continuing (degrade-don't-fail).",
                    winnerMemberId, conversationId);
            }

            // (3) Deactivate each losing offerer's chat member id
            //     (PATCH /api/members/{id}/deactivate) so the auction losers drop
            //     out while history is retained. There is NO channel-member 'pop'
            //     verb in the generated client; member-deactivate is the supported
            //     loser-removal verb. Each is independent + best-effort.
            foreach (var loserMemberId in losingMemberIds)
            {
                if (string.IsNullOrWhiteSpace(loserMemberId)) continue;
                try
                {
                    await chat.Deactivate2Async(loserMemberId, ct);
                }
                catch (Exception deactivateEx)
                {
                    _logger.LogWarning(deactivateEx,
                        "Accept-advance: deactivating losing chat member {MemberId} on channel {ConversationId} failed; continuing.",
                        loserMemberId, conversationId);
                }
            }

            // PHASE NOTE (H6d): flipping the channel's phase to 'accepted' so
            // GET /api/Chat/channels/{id}/summary reports phase="accepted"
            // requires BOTH (a) a chat-service ResolvePhase change to recognise an
            // 'accepted' Tag/Type marker (today it only recognises "broadcasting"
            // and defaults everything else to "direct") AND (b) a chat-service
            // channel-advance endpoint to UPDATE the channel Tag/Type — neither
            // exists in the generated client today. That is a separate chat-service
            // PR + NSwag regen. This gateway method therefore advances the
            // MEMBERSHIP (winner added, losers deactivated); the phase literal flip
            // lands when the chat-service change ships. The accept is never blocked
            // by this gap.
            return winnerMemberId;
        }
        catch (Exception ex)
        {
            // A chat-service outage must NEVER turn a successful offer accept into
            // a 5xx. Degrade: the conversation is left in its prior phase and the
            // accept still returns 200. Mirrors the create-path contract.
            _logger.LogWarning(ex,
                "Accept-advance for conversation {ConversationId} unavailable; accept succeeds, conversation phase unchanged.",
                conversationId);
            return null;
        }
    }

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
            var chat = scope.ServiceProvider.GetRequiredService<ServiceChatClient>();

            // E22 / I3 (Q-036): drive the conversation to closed via the CONSUMED
            // chat-service's EXISTING channel-deactivate verb
            // (PATCH /api/channels/{id}/deactivate). The channel id IS the delivery
            // row's ConversationId minted at create time. This consumes chat-service's
            // own API — no gateway store write (GR-3), no Firestore edit (GR-1), and no
            // chat-service change (the verb already exists, so no owner-approval gate).
            // Deactivating an already-closed channel is an upstream no-op (idempotent),
            // so a duplicate completion signal cannot corrupt state.
            await chat.DeactivateAsync(conversationId, ct);
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
