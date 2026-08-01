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

    /// <summary>
    /// all | per-recipient set. An OPEN shape the gateway carries verbatim (chat
    /// owns its meaning). Typed as <see cref="System.Text.Json.JsonElement"/> and
    /// marshalled by <see cref="RawJsonElementConverter"/> so the STJ-bound value
    /// from the request body ("all" / a structured set) is written to chat-service
    /// as raw JSON — never as the JsonElement struct shape (the H4 bug).
    /// </summary>
    [JsonProperty("audience")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public System.Text.Json.JsonElement? Audience { get; set; }

    /// <summary>Free-text body (text kind).</summary>
    [JsonProperty("body")]
    public string? Body { get; set; }

    /// <summary>
    /// Round-tripped structured payload (structured kind). OPEN shape carried
    /// verbatim — same JsonElement + <see cref="RawJsonElementConverter"/> treatment
    /// as <see cref="Audience"/> so a structured payload survives the
    /// STJ-bind → Newtonsoft-write hop intact.
    /// </summary>
    [JsonProperty("payload")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public System.Text.Json.JsonElement? Payload { get; set; }

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

    /// <summary>
    /// The conversation this message belongs to. chat-service stamps it on every
    /// message projection (<c>ConversationMessageResponse.conversation_id</c>);
    /// declared here so the typed hop relays it instead of silently erasing it
    /// (same omission class as <see cref="CreatedAt"/> — see that remark).
    /// </summary>
    [JsonProperty("conversation_id")]
    [Stj.JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    [JsonProperty("kind")]
    [Stj.JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonProperty("subtype")]
    [Stj.JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonProperty("author_id")]
    [Stj.JsonPropertyName("author_id")]
    public string? AuthorId { get; set; }

    /// <summary>
    /// The audience chat-service stamped on THIS message — round-tripped verbatim.
    /// <see cref="RawJsonElementConverter"/> reads chat-service's raw JSON (Newtonsoft
    /// wire) into a <see cref="System.Text.Json.JsonElement"/>; the STJ response
    /// serializer then emits it natively (e.g. <c>"all"</c>), so the append response
    /// echoes the audience chat-service actually created — not a mangled JObject (H4).
    /// </summary>
    [JsonProperty("audience")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    [Stj.JsonPropertyName("audience")]
    public System.Text.Json.JsonElement? Audience { get; set; }

    /// <summary>
    /// The structured payload chat-service stamped on THIS message — round-tripped
    /// verbatim via <see cref="RawJsonElementConverter"/> (same as <see cref="Audience"/>).
    /// </summary>
    [JsonProperty("payload")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    [Stj.JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; set; }

    [JsonProperty("body")]
    [Stj.JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>
    /// WHEN chat-service created this message — the send time, and the ONLY send
    /// time any reader can trust.
    ///
    /// <para>
    /// THE BILATERAL EMPTY-THREAD DEFECT. This field did not exist on this DTO.
    /// chat-service emits it on every message projection
    /// (<c>ChatService.Domain/Response/ConversationMessageResponse.cs</c> →
    /// <c>[JsonProperty("created_at")] DateTime CreatedAt</c>, confirmed on the LIVE
    /// service: <c>"created_at":"…Z"</c>), but because no property here declared it,
    /// the typed deserialize/re-serialize hop in
    /// <c>JeebConversationClient.SendAsync&lt;JeebMessageListResponse&gt;</c> DROPPED
    /// it, and <c>GET /v1/conversations/{id}/messages</c> answered 200 with rows that
    /// carried no timestamp at all. The mobile client's history decoder rejected
    /// every such row, so a 200 with the whole thread decoded to ZERO messages for
    /// BOTH participants — an empty chat on a healthy API. A dropped field is
    /// invisible on the gateway side: nothing throws, the status is 200, and the
    /// count of messages is right. Only the RECEIVER notices.
    /// </para>
    ///
    /// <para>
    /// TYPE CHOICE — <c>DateTime?</c>, deliberately, not <c>DateTimeOffset?</c> and
    /// not <c>string</c>:
    /// <list type="bullet">
    ///   <item><c>DateTime?</c> mirrors the exact CLR type chat-service declares, and
    ///   Newtonsoft's default <c>DateTimeZoneHandling.RoundtripKind</c> preserves the
    ///   <c>Z</c> as <c>DateTimeKind.Utc</c>, which the STJ response serializer
    ///   re-emits as the same ISO-8601 <c>…Z</c> text. The value the device reads is
    ///   the value chat-service stored.</item>
    ///   <item><c>DateTimeOffset?</c> (the <see cref="JeebConversationParticipant.RemovedAt"/>
    ///   precedent) would SHIFT the instant if chat-service ever emitted an
    ///   offset-less timestamp, because Newtonsoft would then assume the GATEWAY
    ///   host's local offset. A timestamp that is silently wrong by the server's
    ///   UTC offset is worse than one that is missing.</item>
    ///   <item>NULLABLE so an upstream row with no timestamp stays <c>null</c> rather
    ///   than being fabricated as the <c>0001-01-01</c> <c>default(DateTime)</c> husk.
    ///   The client treats both as "no send time" and falls back to the row's
    ///   position, but null is the honest wire signal and keeps the husk out of
    ///   logs and clients that are less forgiving.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// WIRE NAME — <c>created_at</c>, the name chat-service already emits and the
    /// FIRST name the mobile decoder looks for
    /// (<c>dio_chat_gateway.dart</c> → <c>_sentAtOf</c>: <c>createdAt</c> ??
    /// <c>created_at</c> ?? <c>sentAt</c> ?? <c>sent_at</c>). Do not rename it or add
    /// an alias: four spellings already exist in this system and each new one is
    /// another row a reader can fail to date.
    /// </para>
    /// </summary>
    [JsonProperty("created_at")]
    [Stj.JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// The viewer-filtered list chat-service returns for GET messages. chat-service
/// owns the VisibilityFilter (JEB-51) and returns ONLY the messages the supplied
/// viewer may see — the gateway forwards the viewer and re-serializes the result
/// verbatim, computing no visibility itself (no domain leak / REST-WS drift).
/// </summary>
public sealed class JeebMessageListResponse
{
    /// <summary>
    /// The conversation the slice was read from. Emitted by chat-service on the
    /// list envelope; relayed rather than dropped (see
    /// <see cref="JeebMessageResponse.CreatedAt"/> for the omission class).
    /// </summary>
    [JsonProperty("conversation_id")]
    [Stj.JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    /// <summary>
    /// WHO the slice was scoped for. chat-service owns the VisibilityFilter and
    /// echoes the viewer it filtered against so the parity contract (INV-1: the
    /// delta read can never leak a message the full read hides) is auditable ON THE
    /// WIRE instead of only in chat-service's logs. Relayed, never computed here.
    /// </summary>
    [JsonProperty("viewer_id")]
    [Stj.JsonPropertyName("viewer_id")]
    public string? ViewerId { get; set; }

    [JsonProperty("messages")]
    [Stj.JsonPropertyName("messages")]
    public IList<JeebMessageResponse> Messages { get; set; }
        = new List<JeebMessageResponse>();
}

/// <summary>
/// S08 (B) — add-participant body for chat-service's
/// <c>POST /api/conversations/{id}/participants</c> (AddParticipantAsync). The
/// gateway seats the offer jeeber as a member when they submit an offer so the
/// per-jeeber VisibilityFilter lets them read (200) and non-members 403.
/// snake_case wire (chat-service contract); the gateway forwards verbatim.
/// </summary>
public sealed class AddJeebParticipantRequest
{
    [JsonProperty("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>One of: client | jeeber_offerer | jeeber_winner. Offer-submit seats jeeber_offerer.</summary>
    [JsonProperty("role_in_convo")]
    public string RoleInConvo { get; set; } = "jeeber_offerer";
}

/// <summary>
/// S08 (D) — phase-advance body for chat-service's
/// <c>PATCH /api/conversations/{id}/phase</c> (AdvancePhaseAsync). chat-service
/// flips phase, promotes the winner role and soft-removes the other jeebers
/// atomically. The gateway composes this post-accept; it computes no membership.
/// </summary>
public sealed class AdvanceJeebPhaseRequest
{
    /// <summary>broadcasting | accepted | direct. Post-accept the gateway sends "accepted".</summary>
    [JsonProperty("phase")]
    public string Phase { get; set; } = "accepted";

    /// <summary>The winning jeeber's user id — promoted to <c>jeeber_winner</c>.</summary>
    [JsonProperty("winner_user_id")]
    public string? WinnerUserId { get; set; }

    /// <summary>Role to assign the winner. Defaults to <c>jeeber_winner</c>.</summary>
    [JsonProperty("winner_role_in_convo")]
    public string WinnerRoleInConvo { get; set; } = "jeeber_winner";

    /// <summary>When true chat-service soft-removes the non-winning participants (loser kick).</summary>
    [JsonProperty("remove_others")]
    public bool RemoveOthers { get; set; } = true;
}

/// <summary>
/// GW5 / W1.6-gateway — request body for chat-service's ADDITIVE
/// <c>POST /api/conversations/{id}/settle</c> (CB4). ONE call that seats the winner,
/// sets the phase and soft-removes the losing bidders against ONE loaded aggregate,
/// committed by ONE store write.
///
/// <para>Replaces the gateway's <see cref="AddJeebParticipantRequest"/> +
/// <see cref="AdvanceJeebPhaseRequest"/> pair on the post-accept path. Those two
/// requests were issued back-to-back from inside a post-commit best-effort block, so
/// a failure BETWEEN them left the winner seated in a conversation still in its
/// pre-settlement phase with every losing bidder still active — a silent half-state on
/// the only coordination channel a cash handover has.</para>
///
/// <para>CONVERGENT, NOT INCREMENTAL: every field states a desired END STATE, never a
/// delta, so a verbatim replay is the intended recovery action (chat-service answers
/// 200 with <c>already_settled: true</c> and still reconciles its projection). There is
/// no idempotency key and chat-service accepts none — there is no increment to
/// de-duplicate. See <c>chat-service/documentation/CONVERSATION-SETTLE-CONTRACT.md</c>
/// §4, which is the frozen contract this DTO is written against.</para>
///
/// <para>Field names are the chat-service wire names (snake_case), forwarded verbatim.
/// The gateway computes no membership — it names the winner and the phase and lets
/// chat-service, the membership authority, resolve the roster.</para>
/// </summary>
public sealed class SettleJeebConversationRequest
{
    /// <summary>
    /// The phase the conversation must be in when the call RETURNS (not a delta).
    /// Post-accept the gateway sends <c>accepted</c>. Blank ⇒ chat-service 400.
    /// </summary>
    [JsonProperty("phase")]
    public string Phase { get; set; } = "accepted";

    /// <summary>
    /// The participant to SEAT — added when absent, re-activated when previously
    /// removed. This is the field that folds the old separate seat call into the
    /// phase advance. Blank ⇒ chat-service 400.
    /// </summary>
    [JsonProperty("winner_user_id")]
    public string WinnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// Role the seated participant must carry. <c>jeeber_winner</c> — the SAME token
    /// the old two-call sequence seated with, so a conversation half-settled by that
    /// sequence converges rather than being promoted twice under two names.
    /// </summary>
    [JsonProperty("winner_role_in_convo")]
    public string WinnerRoleInConvo { get; set; } = "jeeber_winner";

    /// <summary>
    /// Soft-remove every OTHER active participant in the restricted lane (the losing
    /// bidders). chat-service applies the same predicate <c>AdvancePhaseAsync</c> uses,
    /// and excludes <see cref="WinnerUserId"/> by construction — a single call can
    /// never seat and remove the same participant.
    /// </summary>
    [JsonProperty("remove_others")]
    public bool RemoveOthers { get; set; } = true;
}

/// <summary>
/// GW5 / W1.6-gateway — chat-service's answer to
/// <c>POST /api/conversations/{id}/settle</c>.
///
/// <para><b>READ THIS BEFORE BINDING.</b> The settled conversation is NESTED under
/// <see cref="Conversation"/>; this is NOT a bare <see cref="JeebConversationResponse"/>.
/// Deserializing the settle body straight into <see cref="JeebConversationResponse"/>
/// yields an object with EVERY field empty and throws NOTHING — the members simply are
/// not there. That silent-empty shape is precisely the failure class this repo has been
/// bitten by before (the dropped <c>created_at</c> on
/// <see cref="JeebMessageResponse.CreatedAt"/>, where a 200 carrying the whole thread
/// decoded to zero messages). <see cref="JeebConversationClient.SettleAsync"/> therefore
/// asserts a non-blank <c>conversation.conversation_id</c> and raises
/// <see cref="JeebConversationApiException"/> rather than returning a husk.</para>
///
/// <para>Nesting is chat-service's backward-compatibility decision: it adds a surface
/// without adding one field to <see cref="JeebConversationResponse"/>, the shape every
/// pre-existing conversation route already returns.</para>
///
/// <para>The four outcome flags describe what THIS CALL changed, not what is true of
/// the conversation. A caller that ignores all of them is still correct — the settled
/// state is fully described by <see cref="Conversation"/>.</para>
/// </summary>
public sealed class JeebConversationSettleResponse
{
    /// <summary>
    /// The settled conversation projection — the same shape every other conversation
    /// route returns, nested unchanged.
    /// </summary>
    [JsonProperty("conversation")]
    public JeebConversationResponse? Conversation { get; set; }

    /// <summary>True when THIS call made the winner an active participant it was not
    /// already — added it, or cleared a previous removal.</summary>
    [JsonProperty("seated")]
    public bool Seated { get; set; }

    /// <summary>True when THIS call changed the seated participant's role.</summary>
    [JsonProperty("role_changed")]
    public bool RoleChanged { get; set; }

    /// <summary>True when THIS call changed the conversation phase.</summary>
    [JsonProperty("phase_changed")]
    public bool PhaseChanged { get; set; }

    /// <summary>The participants soft-removed BY THIS CALL. Empty on a replay —
    /// they were already removed.</summary>
    [JsonProperty("removed_user_ids")]
    public IList<string> RemovedUserIds { get; set; } = new List<string>();

    /// <summary>
    /// True when this call changed no roster state and no phase.
    ///
    /// <para><b>This does NOT mean "nothing happened".</b> chat-service reconciles the
    /// direct-read visibility projection UNCONDITIONALLY on every settle, so a replay
    /// reporting <c>already_settled: true</c> is exactly how a projection left
    /// half-written by an earlier partial failure gets repaired. A caller that reads
    /// this flag as "no-op, skip the retry" disables that repair — which is why
    /// <c>AcceptChatSettleReconciler</c> never branches on it.</para>
    /// </summary>
    [JsonProperty("already_settled")]
    public bool AlreadySettled { get; set; }
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
