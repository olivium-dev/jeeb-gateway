using JeebGateway.Push;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Chat;

/// <summary>
/// Reference implementation of <see cref="IChatDispatcher"/>. Per send:
///
///   1. Validate the payload shape for the requested
///      <see cref="ChatMessageType"/> — bad combinations 400 the caller.
///   2. Persist the message (in-memory for MVP; Postgres post-swap).
///   3. Fan out the message to both participants via the SignalR group
///      keyed by <see cref="ConversationKey"/>. Either side may not be
///      connected — that's fine, the message is already persisted and
///      will be replayed via the history endpoint on reconnect.
///   4. If the recipient is NOT foregrounded (no live hub connections,
///      or every connection has reported backgrounded), enqueue a Chat
///      push via <see cref="IPushNotificationService"/>. The push stub
///      handles user-level preference filtering downstream.
/// </summary>
public sealed class ChatDispatcher : IChatDispatcher
{
    private readonly IChatServiceClient _client;
    private readonly IChatPresenceTracker _presence;
    private readonly IHubContext<ChatHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChatDispatcher> _log;

    public ChatDispatcher(
        IChatServiceClient client,
        IChatPresenceTracker presence,
        IHubContext<ChatHub> hub,
        IPushNotificationService push,
        TimeProvider clock,
        ILogger<ChatDispatcher> log)
    {
        _client = client;
        _presence = presence;
        _hub = hub;
        _push = push;
        _clock = clock;
        _log = log;
    }

    public async Task<ChatMessage> SendAsync(string senderId, SendMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(senderId))
            throw new ChatValidationException("sender is required");
        if (string.IsNullOrWhiteSpace(request.RecipientId))
            throw new ChatValidationException("recipientId is required");
        if (string.Equals(senderId, request.RecipientId, StringComparison.Ordinal))
            throw new ChatValidationException("cannot send to self");

        ValidatePayload(senderId, request);

        var conversationId = ConversationKey.For(senderId, request.RecipientId!);

        // Persist to the GENERIC chat-service via the BFF client (the gateway is
        // a pure BFF — no in-memory message record-of-truth). The client owns the
        // member/channel/session aggregation and returns the upstream-assigned id;
        // we adopt it so the hub fan-out and read receipts address the canonical
        // message. The rich type/media/coords fields are gateway-owned domain
        // metadata for the live hub/push fan-out (the generic upstream stores only
        // text + opaque payload), so we keep them on the echoed ChatMessage.
        var persisted = await _client.SendMessageAsync(senderId, request, ct);

        var message = new ChatMessage
        {
            Id = string.IsNullOrEmpty(persisted.Id) ? Guid.NewGuid().ToString() : persisted.Id,
            ConversationId = conversationId,
            SenderId = senderId,
            RecipientId = request.RecipientId!,
            Type = request.Type,
            SentAt = persisted.SentAt == default ? _clock.GetUtcNow() : persisted.SentAt,
            Text = request.Text,
            MediaUrl = request.MediaUrl,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            OfferId = request.OfferId
        };

        var dto = ChatMessageDto.From(message);
        // Group fan-out delivers to whichever side is connected; this
        // satisfies the <1s WS delivery AC without us tracking explicit
        // recipient connection ids.
        await _hub.Clients.Group(conversationId).SendAsync("ReceiveMessage", dto, ct);

        if (!_presence.IsForegrounded(request.RecipientId!))
        {
            // T-backend-022 push pipeline applies the user's chat
            // category preference and handles transport selection.
            var pushResult = await _push.SendAsync(
                new PushNotificationRequest(
                    UserId: request.RecipientId!,
                    Trigger: NotificationTrigger.Chat,
                    Title: "New message",
                    Body: BuildPushBody(message),
                    Data: new Dictionary<string, string>
                    {
                        ["conversationId"] = conversationId,
                        ["messageId"] = message.Id,
                        ["type"] = message.Type.ToString()
                    }),
                ct);
            _log.LogInformation(
                "chat push for backgrounded recipient {Recipient}: {Outcome}",
                request.RecipientId, pushResult.Outcome);
        }

        return message;
    }

    public async Task<ChatMessage?> MarkReadAsync(
        string otherUserId, string messageId, string readerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ChatValidationException("messageId is required");
        if (string.IsNullOrWhiteSpace(readerId))
            throw new ChatValidationException("reader is required");
        if (string.IsNullOrWhiteSpace(otherUserId))
            throw new ChatValidationException("otherUserId is required");

        // Persist the receipt on the generic chat-service via the BFF client.
        // Returns null when the channel/message can't be resolved (idempotent
        // no-op) so a race-y or duplicate MarkRead does not 500 the client.
        var seen = await _client.MarkMessageSeenAsync(readerId, otherUserId, messageId, ct);
        if (seen is null) return null;

        var conversationId = ConversationKey.For(readerId, otherUserId);
        var updated = new ChatMessage
        {
            Id = seen.Id,
            ConversationId = conversationId,
            SenderId = string.IsNullOrEmpty(seen.SenderId) ? otherUserId : seen.SenderId,
            RecipientId = string.IsNullOrEmpty(seen.RecipientId) ? readerId : seen.RecipientId,
            Type = seen.Type,
            SentAt = seen.SentAt,
            Text = seen.Text,
            MediaUrl = seen.MediaUrl,
            Latitude = seen.Latitude,
            Longitude = seen.Longitude,
            OfferId = seen.OfferId,
            ReadAt = seen.ReadAt ?? _clock.GetUtcNow()
        };

        var receipt = new ReadReceiptDto
        {
            MessageId = updated.Id,
            ConversationId = conversationId,
            ReaderId = readerId,
            ReadAt = updated.ReadAt!.Value
        };
        await _hub.Clients.Group(conversationId).SendAsync("ReadReceipt", receipt, ct);

        return updated;
    }

    private static void ValidatePayload(string senderId, SendMessageRequest request)
    {
        switch (request.Type)
        {
            case ChatMessageType.Text:
                if (string.IsNullOrWhiteSpace(request.Text))
                    throw new ChatValidationException("text is required for Text messages");
                break;
            case ChatMessageType.ImageUrl:
            case ChatMessageType.VoiceNoteUrl:
                if (string.IsNullOrWhiteSpace(request.MediaUrl))
                    throw new ChatValidationException("mediaUrl is required for media messages");
                if (!Uri.TryCreate(request.MediaUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new ChatValidationException("mediaUrl must be an http(s) URL");
                break;
            case ChatMessageType.Location:
                if (!request.Latitude.HasValue || !request.Longitude.HasValue)
                    throw new ChatValidationException("latitude and longitude are required for Location messages");
                if (request.Latitude.Value is < -90 or > 90)
                    throw new ChatValidationException("latitude must be in [-90, 90]");
                if (request.Longitude.Value is < -180 or > 180)
                    throw new ChatValidationException("longitude must be in [-180, 180]");
                break;
            case ChatMessageType.OfferCard:
                if (string.IsNullOrWhiteSpace(request.OfferId))
                    throw new ChatValidationException("offerId is required for OfferCard messages");
                break;
            case ChatMessageType.System:
                // System messages are authored by the gateway only — a
                // user client cannot impersonate the system bus.
                throw new ChatValidationException("System messages cannot be sent by users");
            default:
                throw new ChatValidationException($"unknown message type {request.Type}");
        }
    }

    private static string BuildPushBody(ChatMessage message) => message.Type switch
    {
        ChatMessageType.Text => Truncate(message.Text ?? "", 80),
        ChatMessageType.ImageUrl => "📷 Photo",
        ChatMessageType.VoiceNoteUrl => "🎙️ Voice note",
        ChatMessageType.Location => "📍 Location shared",
        ChatMessageType.OfferCard => "💼 New offer",
        _ => "New message"
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
