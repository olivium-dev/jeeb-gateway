using JeebGateway.Conversations.Client;
using JeebGateway.Observability;
using JeebGateway.Requests;
using JeebGateway.Services;
using Microsoft.Extensions.Options;

namespace JeebGateway.Conversations;

/// <summary>
/// What one settle attempt did. Returned rather than logged-and-forgotten so the
/// caller — the accept path OR the reconciler — can report it honestly.
/// </summary>
public enum AcceptChatSettleStatus
{
    /// <summary>Nothing was attempted: the Chat upstream flag is off, or there is no
    /// request row / no winning jeeber to settle for. NOT a failure.</summary>
    Skipped,

    /// <summary>No conversation could be resolved or created, so there was nothing to
    /// settle against. The winner stays unseated until a later pass.</summary>
    Unresolved,

    /// <summary>chat-service accepted the settle. The conversation is in the settled
    /// 1:1 end state this call asked for.</summary>
    Settled,
}

/// <summary>
/// The outcome of one <see cref="IAcceptChatSettler.SettleAsync"/> call.
/// <paramref name="AlreadySettled"/> is chat-service's own flag and is reported, never
/// branched on — see <see cref="JeebConversationSettleResponse.AlreadySettled"/> for why
/// treating it as "no-op" disables the projection repair.
/// </summary>
public sealed record AcceptChatSettleResult(
    AcceptChatSettleStatus Status,
    string? ConversationId,
    bool AlreadySettled);

/// <summary>
/// GW5 / W1.6-gateway — the post-accept chat settlement, in ONE place.
///
/// <para>Extracted from <c>JeebOffersController.EnsureConversationAndSeatWinnerAsync</c>
/// because it now has TWO callers that must behave identically: the accept path (inline,
/// best-effort, post-commit) and <see cref="AcceptChatSettleReconciler"/> (the heal pass
/// that exists precisely because the inline attempt can be lost). Two copies of a money-
/// adjacent saga step is two places to drift; there is one.</para>
///
/// <para>WHAT IT DOES: resolve-or-create the conversation for the request, link its id
/// onto the local projection AND the durable store, then issue ONE
/// <c>POST /api/conversations/{id}/settle</c> that seats the winner, sets phase =
/// <c>accepted</c> and soft-removes the losing bidders. It replaces the previous
/// add-participant → advance-phase pair, whose inter-request window left the winner
/// seated in a pre-settlement conversation with every loser still active whenever the
/// first call landed and the second did not.</para>
///
/// <para>IT THROWS. Deliberately. This type does not decide what a chat-service fault
/// means — the accept path must swallow (its saga has already committed upstream; a chat
/// blip must never turn a committed accept into a 5xx) while the reconciler must count
/// and retry. Swallowing here would take that choice away from both and reproduce the
/// original defect: a failure nobody is told about.</para>
///
/// <para>THE GATEWAY COMPUTES NO MEMBERSHIP. It names the winner, the role and the
/// phase; chat-service — the membership authority, org no-coupling law — resolves the
/// roster. The gateway is also the SOLE chat caller.</para>
/// </summary>
public interface IAcceptChatSettler
{
    /// <summary>
    /// Ensure the request's conversation exists and settle it onto the winning jeeber.
    /// Convergent: safe to call repeatedly with the same arguments, which is exactly
    /// what the reconciler does.
    /// </summary>
    /// <exception cref="JeebConversationApiException">chat-service refused or was
    /// unreachable. The caller decides whether that is fatal.</exception>
    Task<AcceptChatSettleResult> SettleAsync(
        DeliveryRequest? request, string? winningJeeberId, CancellationToken ct);
}

/// <inheritdoc cref="IAcceptChatSettler"/>
public sealed class AcceptChatSettler : IAcceptChatSettler
{
    /// <summary>The role token the winner is settled with. It is the SAME token the
    /// pre-GW5 two-call sequence seated with, so a conversation left half-settled by
    /// that sequence CONVERGES under a settle rather than being promoted twice under
    /// two different names.</summary>
    public const string WinnerRole = "jeeber_winner";

    /// <summary>The phase a settled conversation must be in when the call returns.</summary>
    public const string SettledPhase = "accepted";

    private readonly IJeebConversationClient _conversations;
    private readonly IRequestsStore _requests;
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<AcceptChatSettler> _logger;

    public AcceptChatSettler(
        IJeebConversationClient conversations,
        IRequestsStore requests,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<AcceptChatSettler> logger)
    {
        _conversations = conversations;
        _requests = requests;
        _flags = flags.Value;
        _logger = logger;
    }

    public async Task<AcceptChatSettleResult> SettleAsync(
        DeliveryRequest? request, string? winningJeeberId, CancellationToken ct)
    {
        if (!_flags.Chat
            || request is null
            || string.IsNullOrWhiteSpace(winningJeeberId))
        {
            return new AcceptChatSettleResult(AcceptChatSettleStatus.Skipped, null, false);
        }

        var requestId = request.Id;

        // 1) Resolve the conversation: prefer the id already on the ledger row; else the
        //    chat-service by-correlation lookup (a client may have created it explicitly);
        //    else create it. chat-service de-dups on correlation_key, so a racing create is
        //    safe (INV-3: replay returns the same conversation_id).
        var conversationId = request.ConversationId;

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            try
            {
                var existing = await _conversations.GetConversationByCorrelationAsync(requestId, ct);
                conversationId = existing?.ConversationId;
            }
            catch (JeebConversationApiException)
            {
                // 404 / not-yet-created — fall through to create.
            }
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            var created = await _conversations.CreateConversationAsync(new CreateJeebConversationRequest
            {
                RequestId = requestId,            // -> correlation_key (idempotency authority)
                ClientUserId = request.ClientId,  // -> owner_user_id (seeded role_in_convo = client)
                IdempotencyKey = requestId,
            }, ct);
            conversationId = created?.ConversationId;
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            // COUNTED, not merely logged. This path returns rather than throwing, so
            // neither caller's catch fires — without this the winner is locked out of the
            // thread and every counter reads clean. That combination is exactly how the
            // original defect stayed invisible.
            ChatSettleTelemetry.Failures.Add(1);
            _logger.LogWarning(
                "Post-accept conversation ensure for request {RequestId}: no conversationId "
                + "resolvable or creatable; jeeber {JeeberId} stays unseated until reconciled "
                + "(accept stays 200).", requestId, winningJeeberId);
            return new AcceptChatSettleResult(AcceptChatSettleStatus.Unresolved, null, false);
        }

        // 2) Link the conversation id onto the local projection (in-place stamp on the
        //    live store row, mirroring the create-time auto-create path) so subsequent
        //    reads surface a non-null conversationId.
        //
        //    JEBV4-345: route the stamp THROUGH THE STORE as well. The bare field
        //    assignment below only mutates the in-memory object; on the durable store
        //    that leaves gw_conversation_id NULL in Postgres, so after a gateway bounce
        //    GetByConversationIdAsync can no longer resolve this conversation and the
        //    chat push for this order dies silently. The store call persists it.
        request.ConversationId = conversationId;
        await _requests.SetConversationIdAsync(requestId, conversationId, ct);

        // 3) SEAT AND SETTLE IN ONE CALL. Seats the winner, advances the conversation
        //    OUT of the auction/broadcasting phase into the settled (accepted) 1:1, and
        //    soft-removes the losing bidders — one request, one aggregate load, one store
        //    write, one projection reconcile on the chat side.
        //
        //    The clean 1:1 is not cosmetic: chat-service only grants the winner
        //    visibility of the client's messages when the roster is owner + single
        //    accepted counterpart, so leaving ANY losing bidder seated is simultaneously
        //    a leak risk and the reason the winner reads a blank thread. Before GW5 this
        //    was two independent requests from inside a post-commit block, and the state
        //    between them was exactly that broken roster.
        var settled = await _conversations.SettleAsync(
            conversationId,
            new SettleJeebConversationRequest
            {
                Phase = SettledPhase,
                WinnerUserId = winningJeeberId,
                WinnerRoleInConvo = WinnerRole,
                RemoveOthers = true,
            },
            ct);

        ChatSettleTelemetry.Settled.Add(1);

        _logger.LogInformation(
            "Post-accept chat settle for request {RequestId} (conversation {ConversationId}, "
            + "jeeber {JeeberId}): seated={Seated} phaseChanged={PhaseChanged} "
            + "removed={RemovedCount} alreadySettled={AlreadySettled}.",
            requestId, conversationId, winningJeeberId,
            settled.Seated, settled.PhaseChanged,
            settled.RemovedUserIds?.Count ?? 0, settled.AlreadySettled);

        return new AcceptChatSettleResult(
            AcceptChatSettleStatus.Settled, conversationId, settled.AlreadySettled);
    }
}
