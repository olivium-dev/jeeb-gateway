using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Requests;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// BUILD-CHAT-PUSH — the chat-message → push-notification trigger. When a chat
/// message is appended, the other party of the conversation must receive an FCM
/// push (the only missing link for real A→B chat push; all other rails — Flutter
/// token registration, the gateway PushNotificationController, the push service at
/// :10040 with real FCM creds — are already live).
///
/// <para>Recipient resolution (see <see cref="IRequestsStore.GetByConversationIdAsync"/>):
/// the chat send handlers are keyed by <c>conversationId</c>, but the chat client
/// (<c>IJeebConversationClient</c>) exposes NO participant-roster read keyed by
/// conversationId — only by correlation key (== requestId), which the send path does
/// not have, and the message-append response carries neither. So this notifier
/// resolves the two delivery principals the gateway already owns on the request row
/// — <c>ClientId</c> (requester) and <c>JeeberId</c> (awarded jeeber) — via the
/// conversation-id stamped onto the row at create / accept, and pushes to those
/// minus the author. This covers the A→B (client ↔ winning jeeber) chat the product
/// needs. It does NOT cover the full chat-service participant set (broadcasting-phase
/// offerers) — that needs a real chat-service participants-by-conversationId read and
/// is a follow-up.</para>
///
/// <para>DEGRADE-DON'T-FAIL: this is best-effort. It NEVER throws and never affects
/// the chat send's 201 — every failure (no matching request, push-service blip,
/// timeout) is logged and swallowed. The FCM round-trip is bounded by a short
/// timeout so a slow/down push service cannot materially delay the 201.</para>
///
/// <para>Reuses the EXISTING, deployed <see cref="ServicePushNotificationClient"/>
/// (the same typed client + base URL :10040 that <c>PushNotificationController</c>
/// uses) and its <see cref="SentPayloadToUserRequest"/> contract — no new push
/// contract is invented. The dead in-gateway IPushNotificationService/FcmPushTransport
/// path is intentionally NOT used.</para>
/// </summary>
public interface IChatMessagePushNotifier
{
    /// <summary>
    /// Best-effort: push a "new message" notification to the conversation's other
    /// delivery principal(s) (excluding the author). Never throws.
    /// </summary>
    Task NotifyNewMessageAsync(
        string conversationId,
        string authorUserId,
        string? messagePreview,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class ChatMessagePushNotifier : IChatMessagePushNotifier
{
    // Bounds the FCM fan-out so a slow/down push service cannot materially delay
    // the chat send's 201 (the LAN-local push svc is normally <200ms).
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(2);

    private const int PreviewMaxLength = 120;

    private readonly IRequestsStore _requests;
    private readonly ServicePushNotificationClient _push;
    private readonly ILogger<ChatMessagePushNotifier> _logger;

    public ChatMessagePushNotifier(
        IRequestsStore requests,
        ServicePushNotificationClient push,
        ILogger<ChatMessagePushNotifier> logger)
    {
        _requests = requests;
        _push = push;
        _logger = logger;
    }

    public async Task NotifyNewMessageAsync(
        string conversationId,
        string authorUserId,
        string? messagePreview,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return;
            }

            var request = await _requests.GetByConversationIdAsync(conversationId, ct);
            if (request is null)
            {
                // The conversation id does not map to a known delivery request row
                // (e.g. a legacy chat-service channel id, or a row not locally synced).
                // Best-effort: nothing to resolve recipients from — skip silently.
                _logger.LogDebug(
                    "Chat push: no request row for conversation {ConversationId}; skipping push.",
                    conversationId);
                return;
            }

            // The two delivery principals minus the author. JeeberId is null until accept.
            var recipients = new[] { request.ClientId, request.JeeberId }
                .Where(id => !string.IsNullOrWhiteSpace(id)
                          && !string.Equals(id, authorUserId, StringComparison.Ordinal))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (recipients.Length == 0)
            {
                return;
            }

            // TODO(follow-up): presence-gate (only-when-recipient-not-foregrounded, the
            // old ChatDispatcher behavior). First proof always pushes to the recipient.
            // TODO(follow-up): broadcasting-phase offerers — resolve the full chat-service
            // participant set via a real participants-by-conversationId read.
            // FCM "data" MUST be a flat string-to-string map. The push service at :10040
            // copies each top-level payload entry (other than title/body, which become the
            // FCM notification block) into the FCM data map, stringifying each value. A
            // NESTED dictionary value therefore reaches the Flutter client as a single
            // "data" key holding a stringified, single-quoted pseudo-JSON blob -- which is
            // why the client needs the hoistNestedRoutingFields workaround. Emit the routing
            // fields as FLAT top-level string entries instead, so each lands as its own FCM
            // data key (conversationId, requestId, type) and no client-side parsing is needed.
            var payload = new Dictionary<string, object?>
            {
                ["title"] = "New message",
                ["body"] = BuildPreview(messagePreview),
                ["conversationId"] = conversationId,
                // Emit BOTH camel and snake variants of the request id. The mobile
                // chat deep-link (tap → /chat/:requestId) reads either; keeping both
                // flat top-level keys matches the offer push (Task 1) and means the
                // client needs no key-shape branching between the two notification types.
                ["requestId"] = request.Id,
                ["request_id"] = request.Id,
                ["type"] = "chat",
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);

            foreach (var recipient in recipients)
            {
                try
                {
                    await _push.Send_notification_to_userAsync(
                        recipient,
                        new SentPayloadToUserRequest { Payload = payload },
                        cts.Token);
                }
                catch (Exception ex)
                {
                    // Per-recipient isolation: one recipient's failure must not block the
                    // others, and never the 201.
                    _logger.LogWarning(ex,
                        "Chat push to recipient on conversation {ConversationId} (request {RequestId}) "
                        + "failed; chat send stays 201.", conversationId, request.Id);
                }
            }
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: the chat message was already accepted upstream.
            _logger.LogWarning(ex,
                "Chat push fan-out for conversation {ConversationId} failed; chat send stays 201.",
                conversationId);
        }
    }

    private static string BuildPreview(string? messagePreview)
    {
        if (string.IsNullOrWhiteSpace(messagePreview))
        {
            return "You have a new message";
        }

        var trimmed = messagePreview.Trim();
        return trimmed.Length <= PreviewMaxLength
            ? trimmed
            : trimmed.Substring(0, PreviewMaxLength) + "…";
    }
}
