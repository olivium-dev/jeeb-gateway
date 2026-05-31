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
///           (<c>POST /api/channels/{channelId}/messages</c>),</item>
///     <item>page history newest-first via the GENERIC list endpoint
///           (<c>GET /api/channels/{channelId}/messages?limit&amp;before</c>).</item>
///   </list>
///
/// Because the generic API exposes no lookup-by-external-id, the gateway keeps a
/// shared <see cref="IChatTopologyMap"/> (Redis in production, in-memory in
/// dev/test) caching userId-&gt;memberId and sortedPairKey-&gt;(channelId, sessions).
/// This is BFF state and lives entirely in the gateway; the chat-service is never
/// asked to resolve Jeeb identities.
///
/// The typed "chat" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress + the org-standard pipeline (Bearer forwarding, X-Service-Auth
/// signing, Polly retry / circuit breaker / timeout), so this class never manages
/// auth or resilience directly.
/// </summary>
public sealed class ChatServiceClient : IChatServiceClient
{
    // chat-service serializes responses with Newtonsoft using DEFAULT settings
    // (no CamelCasePropertyNamesContractResolver): properties WITHOUT an explicit
    // [JsonProperty] keep their PascalCase CLR names on the wire, while properties
    // WITH a [JsonProperty("camel")] use the annotated camelCase name. So the
    // response body is a MIXED-CASE envelope:
    //   - PagedList<T> wrapper  -> PascalCase  (NextPageToken, PageCount, TotalCount, Items)
    //   - MessageResponse items -> camelCase   (guid, createdAt, messageId, memberId, ...)
    // JsonSerializerDefaults.Web sets PropertyNameCaseInsensitive = true, so STJ
    // binds either casing on read; we keep explicit [JsonPropertyName] on every
    // wire DTO below to make the seam unambiguous and resistant to future churn.
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    // Generic list endpoint default page size (chat-service clamps to [1, 100]).
    private const int MaxPageSize = 100;

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
            ? wire.ToDto(senderId, otherUserId, pair.ChannelId, _topology)
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
    public Task<IReadOnlyList<ChatMessageDto>> GetConversationAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct) =>
        PageConversationAsync(userId, otherUserId, limit, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChatMessageDto>> GetConversationTranscriptAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct) =>
        PageConversationAsync(userId, otherUserId, limit, ct);

    /// <summary>
    /// Resolves the deterministic channel for the pair and pages the generic
    /// list-messages endpoint (newest-first cursor pagination) until
    /// <paramref name="limit"/> messages are gathered or the channel is exhausted.
    /// Returns the result OLDEST-first so callers render a chronological transcript.
    /// </summary>
    private async Task<IReadOnlyList<ChatMessageDto>> PageConversationAsync(
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

        if (limit <= 0) limit = 50;

        // Page the GENERIC list-messages endpoint newest-first until we have
        // `limit` messages (or run out). Each page returns a nextPageToken cursor
        // (a message id) to pass as `before` for the next, older page.
        var collected = new List<ChatMessageDto>(Math.Min(limit, 256));
        string? before = null;
        do
        {
            var pageSize = Math.Min(limit - collected.Count, MaxPageSize);
            if (pageSize <= 0) break;

            var page = await GetChannelMessagesAsync(channelId, pageSize, before, ct)
                .ConfigureAwait(false);
            if (page.Items.Count == 0) break;

            collected.AddRange(page.Items);
            before = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(before) && collected.Count < limit);

        // Endpoint is newest-first; reverse to oldest-first for a chronological
        // transcript. Cap defensively at `limit` (last page may overshoot).
        if (collected.Count > limit)
            collected.RemoveRange(limit, collected.Count - limit);
        collected.Reverse();
        return collected;
    }

    /// <inheritdoc/>
    public async Task<ChannelMessagePage> GetChannelMessagesAsync(
        string channelId,
        int limit,
        string? beforeMessageId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("channelId is required.", nameof(channelId));

        // chat-service clamps limit to [1, 100]; clamp here too so we never send
        // a degenerate page request and the cursor loop terminates predictably.
        if (limit <= 0) limit = 25;
        if (limit > MaxPageSize) limit = MaxPageSize;

        // GET /api/channels/{channelId}/messages?limit={limit}&before={messageId?}
        var url = $"api/channels/{Uri.EscapeDataString(channelId)}/messages?limit={limit}";
        if (!string.IsNullOrEmpty(beforeMessageId))
            url += $"&before={Uri.EscapeDataString(beforeMessageId)}";

        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return ChannelMessagePage.Empty;
        resp.EnsureSuccessStatusCode();

        var paged = await resp.Content.ReadFromJsonAsync<WirePagedList>(JsonOptions, ct);
        if (paged?.Items is null || paged.Items.Count == 0)
            return ChannelMessagePage.Empty;

        var items = paged.Items
            .Select(m => m.ToDto(channelId, _topology))
            .ToList();

        return new ChannelMessagePage
        {
            Items = items,
            NextPageToken = string.IsNullOrEmpty(paged.NextPageToken) ? null : paged.NextPageToken,
            TotalCount = paged.TotalCount
        };
    }

    /// <inheritdoc/>
    public async Task<ChatMessageDto?> MarkMessageSeenAsync(
        string readerId,
        string otherUserId,
        string messageId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(readerId)
            || string.IsNullOrWhiteSpace(otherUserId)
            || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        var pairKey = PairKey(readerId, otherUserId);
        if (!_topology.TryGetChannel(pairKey, out var channelId) || string.IsNullOrEmpty(channelId))
        {
            // No materialized channel => nothing to mark. Idempotent no-op.
            return null;
        }

        var readerMemberId = _topology.GetMemberOrDefault(readerId);

        // POST /api/channels/{channelId}/messages/{messageId}/seen { memberId }
        var seenBody = new WireSeenRequest { MemberId = readerMemberId };
        using var seenResp = await _http.PostAsJsonAsync(
            $"api/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}/seen",
            seenBody, JsonOptions, ct);

        if (seenResp.StatusCode == HttpStatusCode.NotFound)
            return null; // unknown message => no-op rather than 500
        seenResp.EnsureSuccessStatusCode();

        // Read the canonical message back so ReadAt reflects the server state.
        var getUrl =
            $"api/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}";
        using var getResp = await _http.GetAsync(getUrl, ct);
        if (getResp.StatusCode == HttpStatusCode.NotFound)
            return null;
        getResp.EnsureSuccessStatusCode();

        var wire = await getResp.Content.ReadFromJsonAsync<WireMessageResponse>(JsonOptions, ct);
        if (wire is null) return null;

        var dto = wire.ToDto(readerId, otherUserId, channelId, _topology);
        // The reader has just seen it; if the upstream did not echo a modifiedAt,
        // synthesize a ReadAt so callers see the receipt.
        return dto.ReadAt is not null
            ? dto
            : new ChatMessageDto
            {
                Id = dto.Id,
                ConversationId = dto.ConversationId,
                SenderId = dto.SenderId,
                RecipientId = dto.RecipientId,
                Type = dto.Type,
                Text = dto.Text,
                MediaUrl = dto.MediaUrl,
                Latitude = dto.Latitude,
                Longitude = dto.Longitude,
                OfferId = dto.OfferId,
                SentAt = dto.SentAt,
                ReadAt = DateTimeOffset.UtcNow
            };
    }

    // -------------------------------------------------------------------------
    // BFF orchestration — ensure the generic member/channel/session topology
    // exists for a Jeeb user pair, memoized in the gateway-shared topology map.
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
        return $"{a} {b}";
    }

    // -------------------------------------------------------------------------
    // Wire DTOs — exact shapes of the GENERIC chat-service API. Explicit
    // [JsonPropertyName] on every field; see the JsonOptions note above for why
    // the envelope is PascalCase but the message items are camelCase.
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

    private sealed class WireSeenRequest
    {
        [JsonPropertyName("memberId")] public string MemberId { get; init; } = "";
    }

    private sealed class WireIdentityResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
    }

    /// <summary>
    /// Envelope for <c>GET /api/channels/{channelId}/messages</c>
    /// (<c>PagedList&lt;MessageResponse&gt;</c>). The chat-service PagedList&lt;T&gt;
    /// has NO [JsonProperty], so Newtonsoft (default settings, no camel-case
    /// resolver) emits PascalCase here. STJ Web defaults read it case-insensitively;
    /// we still annotate with the actual wire casing to document the seam.
    /// </summary>
    private sealed class WirePagedList
    {
        [JsonPropertyName("NextPageToken")] public string? NextPageToken { get; init; }
        [JsonPropertyName("PageCount")] public int PageCount { get; init; }
        [JsonPropertyName("TotalCount")] public int TotalCount { get; init; }
        [JsonPropertyName("Items")] public List<WireMessageResponse> Items { get; init; } = new();
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
        /// Projects a generic message onto the Jeeb <see cref="ChatMessageDto"/>,
        /// attributing the message to its authoring Jeeb user. The generic message
        /// stores only the authoring <see cref="MemberId"/>; the topology map's
        /// reverse index resolves it back to a Jeeb userId. The recipient is the
        /// OTHER known channel participant; when only the sender is resolvable the
        /// recipient is left empty rather than guessed.
        /// </summary>
        public ChatMessageDto ToDto(string channelId, IChatTopologyMap topology)
        {
            string sender = MemberId;
            if (!string.IsNullOrEmpty(MemberId) && topology.TryResolveUserByMember(MemberId, out var senderUser))
                sender = senderUser;

            return new ChatMessageDto
            {
                Id = MessageId ?? Guid,
                ConversationId = string.IsNullOrEmpty(ChannelId) ? channelId : ChannelId,
                SenderId = sender,
                RecipientId = string.Empty,
                Type = ChatMessageType.Text,
                Text = Text,
                SentAt = CreatedAt == default ? DateTimeOffset.UtcNow : CreatedAt,
                ReadAt = ModifiedAt
            };
        }

        /// <summary>
        /// Overload used by the single-message read-back paths (send echo,
        /// mark-seen) where both pair members are already known, so sender and
        /// recipient can be attributed precisely.
        /// </summary>
        public ChatMessageDto ToDto(string viewerUserId, string otherUserId, string channelId, IChatTopologyMap topology)
        {
            string sender = viewerUserId;
            string recipient = otherUserId;
            if (!string.IsNullOrEmpty(MemberId) && topology.TryResolveUserByMember(MemberId, out var authorUser))
            {
                sender = authorUser;
                recipient = string.Equals(authorUser, viewerUserId, StringComparison.Ordinal)
                    ? otherUserId
                    : viewerUserId;
            }

            return new ChatMessageDto
            {
                Id = MessageId ?? Guid,
                ConversationId = string.IsNullOrEmpty(ChannelId) ? channelId : ChannelId,
                SenderId = sender,
                RecipientId = recipient,
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
/// Process/cluster-shared BFF state mapping Jeeb identities onto the generic
/// chat-service topology (members, channels, per-member sessions). A 1:1
/// conversation must resolve to the same channel and sessions across requests
/// (and, in production, across replicas) because the generic API cannot look
/// these up by external id.
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

    /// <summary>
    /// Reverse lookup: resolves an authoring generic memberId back to the Jeeb
    /// userId it was created for, or false when the member is unknown to this
    /// gateway. Used to attribute each message in a transcript to the correct
    /// sender (the generic message carries only the authoring memberId).
    /// </summary>
    bool TryResolveUserByMember(string memberId, out string userId);
}

/// <inheritdoc cref="IChatTopologyMap"/>
public sealed class InMemoryChatTopologyMap : IChatTopologyMap
{
    private readonly ConcurrentDictionary<string, string> _members = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _membersReverse = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    // One async-safe gate per logical key prevents duplicate upstream creates
    // when two requests miss the same key concurrently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<string> GetOrAddMemberAsync(string userId, Func<Task<string>> factory)
    {
        var memberId = await GetOrAddAsync(_members, $"member {userId}", userId, factory)
            .ConfigureAwait(false);
        // Maintain the reverse index so a transcript can attribute each message
        // to the Jeeb user that authored it. memberId<->userId is 1:1.
        _membersReverse[memberId] = userId;
        return memberId;
    }

    public Task<string> GetOrAddChannelAsync(string pairKey, Func<Task<string>> factory) =>
        GetOrAddAsync(_channels, $"channel {pairKey}", pairKey, factory);

    public Task<string> GetOrAddSessionAsync(string pairKey, string userId, Func<Task<string>> factory)
    {
        var sessionKey = $"{pairKey} {userId}";
        return GetOrAddAsync(_sessions, $"session {sessionKey}", sessionKey, factory);
    }

    public bool TryGetChannel(string pairKey, out string channelId) =>
        _channels.TryGetValue(pairKey, out channelId!);

    public string GetMemberOrDefault(string userId) =>
        _members.TryGetValue(userId, out var id) ? id : string.Empty;

    public bool TryResolveUserByMember(string memberId, out string userId)
    {
        if (!string.IsNullOrEmpty(memberId) && _membersReverse.TryGetValue(memberId, out userId!))
            return true;
        userId = string.Empty;
        return false;
    }

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
