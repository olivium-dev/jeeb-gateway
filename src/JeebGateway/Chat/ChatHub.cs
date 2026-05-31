using Microsoft.AspNetCore.SignalR;

namespace JeebGateway.Chat;

/// <summary>
/// SignalR hub for real-time chat (T-backend-012). Endpoints:
///
///   - SendMessage(SendMessageRequest)         → echoes the persisted message and fans out "ReceiveMessage" to the conversation group
///   - MarkRead(otherUserId, messageId)        → updates the receipt and fans out "ReadReceipt" to the conversation group
///   - JoinConversation(otherUserId)           → adds the caller to the group keyed by ConversationKey
///   - LeaveConversation(otherUserId)          → removes the caller from the group (optional; auto-cleaned on disconnect)
///   - SetForegroundState(isForeground)        → drives the push-vs-WS routing decision in ChatDispatcher
///
/// Identity: until JWT bearer is wired through the SignalR negotiate
/// path, the hub accepts a "userId" query-string parameter to match the
/// rest of the gateway's MVP X-User-Id pattern. Production wiring will
/// flip to Context.UserIdentifier from the JWT sub claim and the hub
/// resolution helper will collapse to a single line.
/// </summary>
public sealed class ChatHub : Hub
{
    private readonly IChatDispatcher _dispatcher;
    private readonly IChatPresenceTracker _presence;

    public ChatHub(IChatDispatcher dispatcher, IChatPresenceTracker presence)
    {
        _dispatcher = dispatcher;
        _presence = presence;
    }

    public override Task OnConnectedAsync()
    {
        var userId = ResolveUserId();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _presence.Connect(userId, Context.ConnectionId);
        }
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = ResolveUserId();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _presence.Disconnect(userId, Context.ConnectionId);
        }
        return base.OnDisconnectedAsync(exception);
    }

    public async Task<ChatMessageDto> SendMessage(SendMessageRequest request)
    {
        var userId = ResolveUserId() ?? throw new HubException("unauthorized");
        try
        {
            var message = await _dispatcher.SendAsync(userId, request, Context.ConnectionAborted);
            return ChatMessageDto.From(message);
        }
        catch (ChatValidationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task MarkRead(string otherUserId, string messageId)
    {
        var userId = ResolveUserId() ?? throw new HubException("unauthorized");
        try
        {
            await _dispatcher.MarkReadAsync(otherUserId, messageId, userId, Context.ConnectionAborted);
        }
        catch (ChatValidationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task JoinConversation(string otherUserId)
    {
        var userId = ResolveUserId() ?? throw new HubException("unauthorized");
        if (string.IsNullOrWhiteSpace(otherUserId)) throw new HubException("otherUserId is required");
        var conversationId = ConversationKey.For(userId, otherUserId);
        return Groups.AddToGroupAsync(Context.ConnectionId, conversationId);
    }

    public Task LeaveConversation(string otherUserId)
    {
        var userId = ResolveUserId() ?? throw new HubException("unauthorized");
        if (string.IsNullOrWhiteSpace(otherUserId)) throw new HubException("otherUserId is required");
        var conversationId = ConversationKey.For(userId, otherUserId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId);
    }

    public Task SetForegroundState(bool isForeground)
    {
        var userId = ResolveUserId() ?? throw new HubException("unauthorized");
        _presence.SetForegroundState(userId, Context.ConnectionId, isForeground);
        return Task.CompletedTask;
    }

    private string? ResolveUserId()
    {
        // 1. JWT bearer (production): User.Identity.Name once SignalR
        //    negotiate flows the access_token query-string convention.
        var fromClaim = Context.UserIdentifier;
        if (!string.IsNullOrWhiteSpace(fromClaim)) return fromClaim;

        // 2. MVP fallback — matches NotificationPreferencesController and
        //    PushController, but routed through the hub's HttpContext.
        var http = Context.GetHttpContext();
        if (http is null) return null;

        if (http.Request.Headers.TryGetValue("X-User-Id", out var hdr) && !string.IsNullOrWhiteSpace(hdr))
            return hdr.ToString();

        if (http.Request.Query.TryGetValue("userId", out var qs) && !string.IsNullOrWhiteSpace(qs))
            return qs.ToString();

        return null;
    }
}
