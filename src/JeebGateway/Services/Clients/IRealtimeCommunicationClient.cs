namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the shared <c>realtime-comunication-service</c>
/// (Elixir / Phoenix, "LiveComm"; repo <c>olivium-dev/realtime-comunication-service</c>).
/// This is the org's generic real-time transport: a Phoenix <c>Socket</c> with a
/// single <c>topic:*</c> channel (membership-validated join), a Redis-backed
/// replay buffer, and an HTTP <b>ingest</b> seam for background publishers that
/// cannot hold a WebSocket open.
///
/// <para>
/// WHAT THE GATEWAY CALLS. Mobile clients connect the WebSocket directly
/// (Phoenix channel <c>topic:jeeb:chat</c>, join validated against the token's
/// <c>topics</c>/<c>scopes</c> claims — the "membership-validated join" the
/// tickets require). The gateway's role is the SERVER-SIDE FAN-OUT path: when a
/// chat message is accepted (REST <see cref="JeebGateway.Controllers.ChatController"/>
/// or the SignalR hub), the gateway publishes a per-recipient event to the
/// realtime-comunication-service over HTTP so a backgrounded recipient still
/// receives it. That is the ONE upstream route this client wraps:
/// </para>
///
/// <code>POST /api/ingest/{topic}/{stream}</code>
/// <para>
/// verified against <c>realtime-comunication-service/lib/live_comm_web/router.ex</c>
/// and <c>controllers/ingest_controller.ex</c>. Body is
/// <c>{ "data": {...}, "meta": {...} }</c>; the service authenticates the Bearer
/// token, runs <c>ACL.authorize(claims, topic, :publish)</c>, throttles, stores
/// the envelope in the replay buffer, and <c>Phoenix.PubSub.broadcast</c>es it to
/// every WebSocket subscriber of <paramref name="topic"/>. Returns
/// <c>202 { "ok": true, "id": "...", "seq": N }</c>.
/// </para>
///
/// <para>
/// PER-RECIPIENT FAN-OUT FILTER (JEB-50/51/52). The gateway addresses one
/// recipient per publish by encoding the recipient into the <c>stream</c>
/// (<c>user:{recipientId}</c>) under a fixed product <c>topic</c> (<c>jeeb:chat</c>).
/// Subscribers filter on stream so a message only reaches the intended recipient;
/// the gateway never broadcasts a 1:1 message to the whole topic.
/// </para>
///
/// <para>
/// NOT-YET-DEPLOYED. realtime-comunication-service is in the olivium fleet but is
/// NOT on the Jeeb swarm. <c>Services:Realtime:BaseUrl</c> is a marked PLACEHOLDER
/// (<c>http://192.168.2.50:PORT_TBD/</c>) in appsettings.Production.json; the
/// <c>FeatureFlags:UseUpstream:Realtime</c> kill switch is OFF in every
/// environment. This client is wired (named HttpClient + bearer +
/// X-Service-Auth + Polly resilience via the standard pipeline) and tested, but
/// must not be flipped on until the service is deployed and the placeholder is
/// replaced with the real host:port.
/// </para>
///
/// The named "realtime" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress (<c>Services:Realtime:BaseUrl</c>) + the org-standard bearer /
/// X-Service-Auth / resilience chain, so this class never thinks about
/// retry/timeout/circuit-breaker.
///
/// All methods throw <see cref="HttpRequestException"/> on a non-2xx the wire
/// layer does not translate.
/// </summary>
public interface IRealtimeCommunicationClient
{
    /// <summary>
    /// Publishes a single event to <paramref name="topic"/>/<paramref name="stream"/>
    /// via <c>POST /api/ingest/{topic}/{stream}</c>. The upstream fans the
    /// envelope out to every WebSocket subscriber of the topic and persists it in
    /// the replay buffer. Maps the Jeeb payload to the ingest body
    /// (<c>data</c> + <c>meta</c>).
    /// </summary>
    /// <param name="topic">
    /// The product topic — Jeeb chat uses <c>jeeb:chat</c>. Authorized upstream
    /// against the caller's <c>topics</c>/<c>scopes</c> claims.
    /// </param>
    /// <param name="stream">
    /// The per-recipient stream — Jeeb encodes the recipient as
    /// <c>user:{recipientId}</c> so only that recipient's subscription receives
    /// the message (per-recipient fan-out filter).
    /// </param>
    /// <param name="data">The chat payload (message id, sender, body, type, ...).</param>
    /// <param name="meta">
    /// Optional envelope metadata merged into the upstream meta (the service
    /// always stamps <c>user_id</c>/<c>tenant</c>/<c>via</c> itself).
    /// </param>
    Task<RealtimePublishResult> PublishAsync(
        string topic,
        string stream,
        IReadOnlyDictionary<string, object?> data,
        IReadOnlyDictionary<string, object?>? meta,
        CancellationToken ct);

    /// <summary>
    /// Convenience fan-out for a Jeeb 1:1 chat message: publishes under the fixed
    /// <c>jeeb:chat</c> topic to the recipient's <c>user:{recipientId}</c> stream.
    /// This is the seam <see cref="JeebGateway.Controllers.RealtimeController"/>
    /// and the chat dispatcher call.
    /// </summary>
    Task<RealtimePublishResult> FanOutChatMessageAsync(
        string recipientId,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct);
}

/// <summary>
/// Mirror of realtime-comunication-service's ingest <c>202</c> body
/// (<c>{ "ok": true, "id": "...", "seq": N }</c>).
/// </summary>
public sealed class RealtimePublishResult
{
    /// <summary>Whether the upstream accepted the publish.</summary>
    public bool Ok { get; init; }

    /// <summary>The server-assigned envelope id.</summary>
    public string? Id { get; init; }

    /// <summary>The monotonic per-(user,topic,stream) sequence number.</summary>
    public long Seq { get; init; }
}
