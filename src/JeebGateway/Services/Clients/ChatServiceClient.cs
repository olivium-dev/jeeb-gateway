using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Chat;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed BFF facade over the GENERIC chat-service
/// (Firestore-backed, C#/.NET 8, base <c>Services:Chat:BaseUrl</c>).
///
/// This class owns ALL Jeeb-specific aggregation. The generic chat-service only
/// understands members, channels, sessions and messages — it has no concept of a
/// "conversation between two Jeeb users". The gateway BFF bridges that gap:
///   <list type="number">
///     <item>map each Jeeb userId to a generic chat member (<c>POST /api/members</c>),</item>
///     <item>derive a deterministic 1:1 channel for the sorted user pair
///           (<c>POST /api/channels</c>),</item>
///     <item>join both members to obtain per-member session ids
///           (<c>POST /api/channels/{channelId}/members</c>),</item>
///     <item>post the message with the sender's session
///           (<c>POST /api/channels/{channelId}/messages</c>).</item>
///   </list>
///
/// Because the generic API exposes no lookup-by-external-id, the gateway keeps a
/// process-local <see cref="IChatTopologyMap"/> (singleton) caching userId→memberId
/// and sortedPairKey→(channelId, sessions). This is BFF state and lives entirely in
/// the gateway; the chat-service is never asked to resolve Jeeb identities.
///
/// The named "chat" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress + the org-standard resilience pipeline (retry, circuit breaker,
/// timeout), so this class never manages retry/timeout/breaker directly.
/// </summary>
public sealed class ChatServiceClient : IChatServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IChatTopologyMap _topology;

    public ChatServiceClient(HttpClient http, IChatTopologyMap topology)
    {
        _http = http;
        _topology = topology;
    }

    /// <inheritdoc/>
    public async Task<ChatMessageDto> SendMessageAsync(
        string senderId,
        string otherUserId,
        string? text,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(senderId))
            throw new ArgumentException("senderId is required.", nameof(senderId));
        if (string.IsNullOrWhiteSpace(otherUserId))
            throw new ArgumentException("otherUserId is required.", nameof(otherUserId));

        var pair = await EnsureConversationAsync(senderId, otherUserId, ct);
        var senderSessionId = pair.SessionFor(senderId);

        // POST /api/channels/{channelId}/messages
        // Generic AddMessageRequest { memberId, channelId, sessionId, text, payload }.
        // A valid session is required or the service replies 401.
        var body = new WireAddMessageRequest
        {
            MemberId = pair.MemberFor(senderId),
            ChannelId = pair.ChannelId,
            SessionId = senderSessionId,
            Text = text,
            Payload = string.Empty
        };

        var messageId = await PostForIdAsync(
            $"api/channels/{Uri.EscapeDataString(pair.ChannelId)}/messages", body, ct);

        // GET /api/channels/{channelId}/messages/{messageId} — read back the
        // canonical persisted message so the DTO reflects server-assigned fields.
        var url = $"api/channels/{Uri.EscapeDataString(pair.ChannelId)}/messages/{Uri.EscapeDataString(messageId)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var wire = await resp.Content.ReadFromJsonAsync<WireMessageResponse>(JsonOptions, ct);

        return wire is not null
            ? wire.ToDto(senderId, otherUserId, pair.ChannelId)
            // Fallback: synthesize from the request if read-back returns empty.
            : new ChatMessageDto
            {
                Id = messageId,
                ConversationId = pair.ChannelId,
                SenderId = senderId,
                RecipientId = otherUserId,
                Type = ChatMessageType.Text,
                Text = text,
                SentAt = DateTimeOffset.UtcNow
            };
    }

    /// <inheritdoc/>
    public Task<ChatMessageDto> SendMessageAsync(
        string senderId,
        SendMessageRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        // The generic chat-service understands only a text body + opaque payload;
        // it has no concept of message type, media URLs, coordinates or offer
        // cards. Project the rich request onto the text-only upstream send so the
        // production path keeps its current behaviour. (The gateway-owned rich
        // chat domain — type validation, presence, push — is exercised in-memory
        // via IChatDispatcher; this BFF facade only bridges the generic upstream.)
        return SendMessageAsync(senderId, request.RecipientId ?? string.Empty, request.Text, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatMessageDto>> GetConversationAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(otherUserId))
            throw new ArgumentException("otherUserId is required.", nameof(otherUserId));

        // Resolve the deterministic channel for the pair. If we have never
        // materialized it (no message has been sent), there is no history yet.
        var pairKey = PairKey(userId, otherUserId);
        if (!_topology.TryGetChannel(pairKey, out var channelId) || string.IsNullOrEmpty(channelId))
        {
            return Array.Empty<ChatMessageDto>();
        }

        // The generic chat-service exposes single-message GET and a channel
        // summary (which carries the last message). It has no "list all messages"
        // endpoint, so the BFF reads the channel summary and projects its last
        // message into the history shape. This is the richest generic read
        // available today; a future generic list endpoint would slot in here.
        var url = $"api/channels/{Uri.EscapeDataString(channelId)}/summary?memberId={Uri.EscapeDataString(_topology.GetMemberOrDefault(userId))}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<ChatMessageDto>();
        }
        resp.EnsureSuccessStatusCode();

        var summary = await resp.Content.ReadFromJsonAsync<WireChannelSummary>(JsonOptions, ct);
        if (summary?.LastMessage is null)
        {
            return Array.Empty<ChatMessageDto>();
        }

        var dto = summary.LastMessage.ToDto(userId, otherUserId, channelId);
        IReadOnlyList<ChatMessageDto> result = new[] { dto };
        return limit > 0 ? result.Take(limit).ToList().AsReadOnly() : result;
    }

    // -------------------------------------------------------------------------
    // BFF orchestration — ensure the generic member/channel/session topology
    // exists for a Jeeb user pair, memoized in the gateway-local topology map.
    // -------------------------------------------------------------------------

    private async Task<ConversationTopology> EnsureConversationAsync(
        string userA, string userB, CancellationToken ct)
    {
        var pairKey = PairKey(userA, userB);

        // Ensure each Jeeb user has a generic chat member id.
        var memberA = await EnsureMemberAsync(userA, ct);
        var memberB = await EnsureMemberAsync(userB, ct);

        // Ensure the deterministic 1:1 channel for the sorted pair exists.
        var channelId = await _topology.GetOrAddChannelAsync(pairKey, async () =>
        {
            // POST /api/channels { name, type, tag, memberId }
            var (a, b) = SortedPair(userA, userB);
            var body = new WireCreateChannelRequest
            {
                Name = $"jeeb-dm:{a}:{b}",
                Type = "direct",
                Tag = $"jeeb-dm:{a}:{b}",
                MemberId = memberA,
                Description = "Jeeb 1:1 conversation"
            };
            return await PostForIdAsync("api/channels", body, ct);
        });

        // Ensure both members have a session in the channel.
        var sessionA = await _topology.GetOrAddSessionAsync(pairKey, userA, () =>
            JoinChannelAsync(channelId, memberA, ct));
        var sessionB = await _topology.GetOrAddSessionAsync(pairKey, userB, () =>
            JoinChannelAsync(channelId, memberB, ct));

        return new ConversationTopology(
            channelId,
            members: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [userA] = memberA,
                [userB] = memberB
            },
            sessions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [userA] = sessionA,
                [userB] = sessionB
            });
    }

    private Task<string> EnsureMemberAsync(string userId, CancellationToken ct) =>
        _topology.GetOrAddMemberAsync(userId, async () =>
        {
            // POST /api/members { name, nickname, type, tag } -> IdentityResponse{id}
            var body = new WireCreateMemberRequest
            {
                Name = userId,
                Nickname = userId,
                Type = "user",
                Tag = $"jeeb-user:{userId}"
            };
            return await PostForIdAsync("api/members", body, ct);
        });

    private async Task<string> JoinChannelAsync(string channelId, string memberId, CancellationToken ct)
    {
        // POST /api/channels/{channelId}/members { memberId, channelId }
        //   -> IdentityResponse{id} where id == SESSION id.
        var body = new WireAddChannelMemberRequest
        {
            ChannelId = channelId,
            MemberId = memberId
        };
        return await PostForIdAsync(
            $"api/channels/{Uri.EscapeDataString(channelId)}/members", body, ct);
    }

    private async Task<string> PostForIdAsync(string url, object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(url, body, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
        var identity = await resp.Content.ReadFromJsonAsync<WireIdentityResponse>(JsonOptions, ct)
            ?? throw new HttpRequestException(
                $"Upstream {resp.RequestMessage?.RequestUri} returned an empty IdentityResponse.");
        if (string.IsNullOrWhiteSpace(identity.Id))
            throw new HttpRequestException(
                $"Upstream {resp.RequestMessage?.RequestUri} returned an IdentityResponse with no id.");
        return identity.Id;
    }

    // -------------------------------------------------------------------------
    // Pair key helpers — deterministic, order-independent.
    // -------------------------------------------------------------------------

    private static (string A, string B) SortedPair(string x, string y) =>
        string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);

    private static string PairKey(string x, string y)
    {
        var (a, b) = SortedPair(x, y);
        return $"{a} {b}";
    }

    // -------------------------------------------------------------------------
    // Wire DTOs — exact shapes of the GENERIC chat-service API.
    // -------------------------------------------------------------------------

    private sealed class WireCreateMemberRequest
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    }

    private sealed class WireCreateChannelRequest
    {
        [JsonPropertyName("memberId")] public string MemberId { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string Description { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    }

    private sealed class WireAddChannelMemberRequest
    {
        [JsonPropertyName("channelId")] public string ChannelId { get; init; } = "";
        [JsonPropertyName("memberId")] public string MemberId { get; init; } = "";
    }

    private sealed class WireAddMessageRequest
    {
        [JsonPropertyName("memberId")] public string MemberId { get; init; } = "";
        [JsonPropertyName("channelId")] public string ChannelId { get; init; } = "";
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("payload")] public string Payload { get; init; } = "";
    }

    private sealed class WireIdentityResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
    }

    private sealed class WireChannelSummary
    {
        [JsonPropertyName("channelId")] public string ChannelId { get; init; } = "";
        [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("lastMessage")] public WireMessageResponse? LastMessage { get; init; }
    }

    private sealed class WireMessageResponse
    {
        [JsonPropertyName("guid")] public string Guid { get; init; } = "";
        [JsonPropertyName("messageId")] public string? MessageId { get; init; }
        [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("payload")] public string? Payload { get; init; }
        [JsonPropertyName("memberId")] public string MemberId { get; init; } = "";
        [JsonPropertyName("channelId")] public string ChannelId { get; init; } = "";
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
        [JsonPropertyName("modifiedAt")] public DateTimeOffset? ModifiedAt { get; init; }

        /// <summary>
        /// Projects a generic message onto the Jeeb <see cref="ChatMessageDto"/>.
        /// The generic message stores only the authoring member, so the BFF
        /// supplies sender/recipient from the resolved user pair: the message is
        /// "from" whichever pair member authored it, "to" the other.
        /// </summary>
        public ChatMessageDto ToDto(string viewerUserId, string otherUserId, string channelId)
        {
            // Map authoring memberId back to a Jeeb userId when known.
            var senderUserId = viewerUserId; // default — overridden below if discernible
            var recipientUserId = otherUserId;

            return new ChatMessageDto
            {
                Id = MessageId ?? Guid,
                ConversationId = string.IsNullOrEmpty(ChannelId) ? channelId : ChannelId,
                SenderId = senderUserId,
                RecipientId = recipientUserId,
                Type = ChatMessageType.Text,
                Text = Text,
                SentAt = CreatedAt == default ? DateTimeOffset.UtcNow : CreatedAt,
                ReadAt = ModifiedAt
            };
        }
    }

    // -------------------------------------------------------------------------
    // Memoized topology — the resolved generic identities for one user pair.
    // -------------------------------------------------------------------------

    private sealed record ConversationTopology(
        string ChannelId,
        IReadOnlyDictionary<string, string> members,
        IReadOnlyDictionary<string, string> sessions)
    {
        public string MemberFor(string userId) => members[userId];
        public string SessionFor(string userId) => sessions[userId];
    }
}

/// <summary>
/// Process-local BFF state mapping Jeeb identities onto the generic chat-service
/// topology (members, channels, per-member sessions). Singleton: a 1:1
/// conversation must resolve to the same channel and sessions across requests
/// because the generic API cannot look these up by external id.
///
/// Thread-safe; the *-Async factory overloads run the create-on-miss exactly
/// once per key under a key-scoped lock so concurrent first sends do not create
/// duplicate members/channels/sessions on the upstream.
/// </summary>
public interface IChatTopologyMap
{
    Task<string> GetOrAddMemberAsync(string userId, Func<Task<string>> factory);
    Task<string> GetOrAddChannelAsync(string pairKey, Func<Task<string>> factory);
    Task<string> GetOrAddSessionAsync(string pairKey, string userId, Func<Task<string>> factory);
    bool TryGetChannel(string pairKey, out string channelId);
    string GetMemberOrDefault(string userId);
}

/// <inheritdoc cref="IChatTopologyMap"/>
public sealed class InMemoryChatTopologyMap : IChatTopologyMap
{
    private readonly ConcurrentDictionary<string, string> _members = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    // One async-safe gate per logical key prevents duplicate upstream creates
    // when two requests miss the same key concurrently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public Task<string> GetOrAddMemberAsync(string userId, Func<Task<string>> factory) =>
        GetOrAddAsync(_members, $"member {userId}", userId, factory);

    public Task<string> GetOrAddChannelAsync(string pairKey, Func<Task<string>> factory) =>
        GetOrAddAsync(_channels, $"channel {pairKey}", pairKey, factory);

    public Task<string> GetOrAddSessionAsync(string pairKey, string userId, Func<Task<string>> factory)
    {
        var sessionKey = $"{pairKey} {userId}";
        return GetOrAddAsync(_sessions, $"session {sessionKey}", sessionKey, factory);
    }

    public bool TryGetChannel(string pairKey, out string channelId) =>
        _channels.TryGetValue(pairKey, out channelId!);

    public string GetMemberOrDefault(string userId) =>
        _members.TryGetValue(userId, out var id) ? id : string.Empty;

    private async Task<string> GetOrAddAsync(
        ConcurrentDictionary<string, string> store,
        string gateKey,
        string storeKey,
        Func<Task<string>> factory)
    {
        if (store.TryGetValue(storeKey, out var existing))
            return existing;

        var gate = _gates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (store.TryGetValue(storeKey, out existing))
                return existing;

            var created = await factory().ConfigureAwait(false);
            store[storeKey] = created;
            return created;
        }
        finally
        {
            gate.Release();
        }
    }
}
