using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Stj = System.Text.Json.Serialization;

namespace JeebGateway.Conversations.Client;

/// <summary>
/// S08 (JEB-50/51/52/53) — the wire contracts for chat-service's NET-NEW Jeeb
/// <b>conversation aggregate</b> (conversation_id / correlation_key /
/// participants[role_in_convo, removed_at] / structured kind+subtype+audience /
/// per-viewer filtered read / phase). These are the DTOs the gateway BFF
/// exchanges with chat-service over <see cref="IJeebConversationClient"/>.
///
/// <para>
/// WHY THESE LIVE IN THE GATEWAY AS HAND-AUTHORED CONTRACTS (not NSwag-generated
/// yet): chat-service owns the conversation domain (ARCH LAW — chat is the chat
/// domain owner; the gateway holds NO conversation state and computes NO
/// visibility). The conversation aggregate is being added to chat-service in a
/// parallel, sequenced PR (verify fix_plan PR-1, chat-service first). Until that
/// upstream contract ships and the gateway can run <c>regenerate-clients.sh</c>
/// against the live chat-service OpenAPI, the gateway defines the agreed contract
/// here — the SAME hand-authored-typed-client precedent the repo already uses for
/// <c>BanServiceClient</c> (see <c>scripts/regenerate-clients.sh</c>: "client is
/// HAND-CODED … not NSwag-generated"). When chat-service's conversation endpoints
/// land, these become the regeneration target and the diff is reviewed against
/// the live spec. The gateway never invents domain logic — these are pure DTOs.
/// </para>
///
/// JSON is Newtonsoft (the repo-wide serializer for chat clients) and every wire
/// field uses the snake_case the S08 scenario asserts (conversation_id,
/// correlation_key, role_in_convo, removed_at, author_id, message_id).
/// </summary>
public sealed class CreateJeebConversationRequest
{
    // chat-service's CreateConversationRequest is the canonical contract:
    //   { correlation_key, owner_user_id, owner_role_in_convo?, phase? }
    // The gateway translates client vocabulary (request_id / client_user_id) onto
    // it on the wire. The C# property names stay request-shaped so the controller
    // assignment is unchanged; only the JSON field names are the chat-service ones.
    // correlation_key IS the idempotency authority (replay returns the same
    // conversation_id, INV-3) — there is no separate idempotency_key field upstream.
    [JsonProperty("correlation_key")]
    public string RequestId { get; set; } = string.Empty;

    [JsonProperty("owner_user_id")]
    public string ClientUserId { get; set; } = string.Empty;

    /// <summary>
    /// The conversation owner's role. The H1 client-created conversation seeds the
    /// owner as <c>client</c> (participants[0].role_in_convo == "client", INV-3).
    /// </summary>
    [JsonProperty("owner_role_in_convo")]
    public string OwnerRoleInConvo { get; set; } = "client";

    /// <summary>
    /// Initial phase. H1 asserts the created conversation is in <c>broadcasting</c>
    /// (offers are still arriving); chat-service advances it to <c>accepted</c> on
    /// the post-accept membership flip (H7). Defaulted here so the gateway pins the
    /// create-time phase rather than relying on a chat-service default.
    /// </summary>
    [JsonProperty("phase")]
    public string Phase { get; set; } = "broadcasting";

    /// <summary>
    /// Forwarded Idempotency-Key (== request_id for H1/A1). NOT serialized onto the
    /// chat-service wire (correlation_key is the idempotency authority); retained so
    /// the controller can keep assigning it without a compile break. JsonIgnore keeps
    /// it off the request body.
    /// </summary>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// One conversation participant — role + soft-removal marker. Dual-annotated:
/// <see cref="JsonPropertyAttribute"/> (Newtonsoft) governs the chat-service wire
/// the typed client marshals; <see cref="Stj.JsonPropertyNameAttribute"/>
/// (System.Text.Json) governs the snake_case the ASP.NET response serializer
/// emits to the caller (the S08 suite asserts <c>role_in_convo</c> / <c>removed_at</c>
/// body-strict). Both name the same wire field so REST-out and client-wire agree.
/// </summary>
public sealed class JeebConversationParticipant
{
    [JsonProperty("user_id")]
    [Stj.JsonPropertyName("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>One of: client | jeeber_offerer | jeeber_winner.</summary>
    [JsonProperty("role_in_convo")]
    [Stj.JsonPropertyName("role_in_convo")]
    public string RoleInConvo { get; set; } = string.Empty;

    /// <summary>Set (~T_accept) when the participant is removed; null while active.</summary>
    [JsonProperty("removed_at")]
    [Stj.JsonPropertyName("removed_at")]
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>
/// The conversation projection chat-service returns on create / membership read.
/// Dual-annotated (Newtonsoft wire + System.Text.Json response) — see
/// <see cref="JeebConversationParticipant"/> for the why.
/// </summary>
public sealed class JeebConversationResponse
{
    [JsonProperty("conversation_id")]
    [Stj.JsonPropertyName("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Equals the originating request_id (auto-conversation-per-request).</summary>
    [JsonProperty("correlation_key")]
    [Stj.JsonPropertyName("correlation_key")]
    public string CorrelationKey { get; set; } = string.Empty;

    /// <summary>broadcasting | accepted | direct.</summary>
    [JsonProperty("phase")]
    [Stj.JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonProperty("participants")]
    [Stj.JsonPropertyName("participants")]
    public IList<JeebConversationParticipant> Participants { get; set; }
        = new List<JeebConversationParticipant>();
}

/// <summary>
/// A structured/text message to append. The gateway NEVER supplies author_id from
/// the body — chat-service stamps it from the viewer the gateway forwards (the
/// bearer sub), so a caller cannot post as another user.
/// </summary>
public sealed class AppendJeebMessageRequest
{
    /// <summary>text | structured.</summary>
    [JsonProperty("kind")]
    public string? Kind { get; set; }

    /// <summary>e.g. jeeb.offer | jeeb.offer_accepted | jeeb.offer_rejected (structured only).</summary>
    [JsonProperty("subtype")]
    public string? Subtype { get; set; }

    /// <summary>all | per-recipient set. Defaults applied by chat-service when omitted.</summary>
    [JsonProperty("audience")]
    public object? Audience { get; set; }

    /// <summary>Free-text body (text kind).</summary>
    [JsonProperty("body")]
    public string? Body { get; set; }

    /// <summary>Round-tripped structured payload (structured kind).</summary>
    [JsonProperty("payload")]
    public object? Payload { get; set; }

    /// <summary>Author resolved from the bearer by the gateway — NEVER from caller body.</summary>
    [JsonProperty("author_id")]
    public string AuthorId { get; set; } = string.Empty;

    /// <summary>Idempotency-Key forwarded verbatim; chat-service de-dups (A2).</summary>
    [JsonProperty("idempotency_key")]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// The message projection chat-service returns on append. Dual-annotated
/// (Newtonsoft wire + System.Text.Json response): the S08 suite asserts
/// <c>message_id</c> / <c>author_id</c> body-strict on the append response.
/// </summary>
public sealed class JeebMessageResponse
{
    [JsonProperty("message_id")]
    [Stj.JsonPropertyName("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [JsonProperty("kind")]
    [Stj.JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonProperty("subtype")]
    [Stj.JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonProperty("author_id")]
    [Stj.JsonPropertyName("author_id")]
    public string? AuthorId { get; set; }

    [JsonProperty("audience")]
    [Stj.JsonPropertyName("audience")]
    public object? Audience { get; set; }

    [JsonProperty("payload")]
    [Stj.JsonPropertyName("payload")]
    public object? Payload { get; set; }

    [JsonProperty("body")]
    [Stj.JsonPropertyName("body")]
    public string? Body { get; set; }
}

/// <summary>
/// The viewer-filtered list chat-service returns for GET messages. chat-service
/// owns the VisibilityFilter (JEB-51) and returns ONLY the messages the supplied
/// viewer may see — the gateway forwards the viewer and re-serializes the result
/// verbatim, computing no visibility itself (no domain leak / REST-WS drift).
/// </summary>
public sealed class JeebMessageListResponse
{
    [JsonProperty("messages")]
    [Stj.JsonPropertyName("messages")]
    public IList<JeebMessageResponse> Messages { get; set; }
        = new List<JeebMessageResponse>();
}

/// <summary>
/// chat-service's answer to "is {viewer} an active participant of {conversation}?".
/// The single membership read that backs both the REST 403 gate (N1/N2) and the
/// WS-ticket issue path. chat-service is the membership authority.
/// </summary>
public sealed class JeebConversationMembership
{
    [JsonProperty("is_member")]
    public bool IsMember { get; set; }

    /// <summary>The viewer's role while active (null when not / no longer a member).</summary>
    [JsonProperty("role_in_convo")]
    public string? RoleInConvo { get; set; }

    /// <summary>Set if the viewer WAS a member but was removed (cutoff read still allowed).</summary>
    [JsonProperty("removed_at")]
    public DateTimeOffset? RemovedAt { get; set; }
}
