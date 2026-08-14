using System;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Realtime;
using JeebGateway.Services;
using JeebGateway.Tracking;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// The customer's entry point to live courier position:
/// <c>GET /v1/realtime/{tenant}:delivery:{deliveryId}</c> (tenant default <c>jeeb</c>).
///
/// <para>Mirrors the chat gate <c>GET /v1/realtime/{tenant}:chat:{conversationId}</c>
/// (<see cref="JeebConversationsController.RealtimeVisibilityGate"/>) exactly: authorize
/// against the authoritative service, then hand back a descriptor naming the topic to
/// subscribe to plus a short-lived credential to subscribe with. The gateway never
/// proxies the socket — proxying it would put a long-lived server-side stream back into
/// the gateway, which is the whole class of thing
/// <c>Tracking/NoBackendPollOrFirestoreListenerGuardTests</c> stands guard over.</para>
///
/// <para><b>Why the credential is minted here and not fetched.</b>
/// realtime-comunication-service exposes an open, unauthenticated
/// <c>POST /api/auth/token</c> that will mint <c>topics:["*"]</c> for anyone who asks.
/// Routing the client through it would mean the customer's access to a delivery is
/// bounded by nothing. The gateway already authenticated the caller and already knows,
/// via delivery-service, which delivery is theirs — so it issues the credential itself,
/// scoped to that one delivery, subscribe-only, minutes-long. A customer who tries the
/// neighbouring delivery's topic with it is refused by the realtime ACL, not merely by
/// our UI.</para>
/// </summary>
[ApiController]
[Produces("application/json")]
public sealed class DeliveryPositionRealtimeController : ControllerBase
{
    private readonly IDeliveryParticipantResolver _participants;
    private readonly IRealtimeGuardianTokenIssuer _guardian;
    private readonly RealtimeGuardianOptions _realtimeOptions;
    private readonly RealtimeTopicNames _topics;
    private readonly UpstreamFeatureFlags _flags;

    public DeliveryPositionRealtimeController(
        IDeliveryParticipantResolver participants,
        IRealtimeGuardianTokenIssuer guardian,
        IOptions<RealtimeGuardianOptions> realtimeOptions,
        RealtimeTopicNames topics,
        IOptions<UpstreamFeatureFlags> flags)
    {
        _participants = participants;
        _guardian = guardian;
        _realtimeOptions = realtimeOptions.Value;
        _topics = topics;
        _flags = flags.Value;
    }

    /// <summary>
    /// Tell an authorized party what to subscribe to for this delivery's live courier
    /// position, and give them the credential to do it with.
    /// </summary>
    // {tenant} is constrained to the configured prefix + the legacy alias, so the
    // pre-rename literal URL keeps matching byte-for-byte and unknown tenants 404.
    [HttpGet("v1/realtime/{tenant}:delivery:{deliveryId}")]
    [AcceptedRealtimeTenant]
    [Authorize]
    // ADR-005 L2 §C client-only delivery tracking: same capability as the one-shot
    // snapshot GET /deliveries/{id}/tracking, because this is that same read moved to a
    // subscription. Party-on-delivery ownership stays in-action (delivery-service is the
    // authority), exactly as the snapshot route does it.
    [RequireCapability(Capabilities.DeliveryTrackOwn)]
    [ProducesResponseType(typeof(DeliveryPositionChannelDescriptor), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPositionChannel(
        string tenant, string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var viewerId, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Realtime)
        {
            return Problem(
                title: "Realtime courier position is not enabled.",
                detail: "FeatureFlags:UseUpstream:Realtime is off, so there is no realtime "
                    + "transport to subscribe to.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var topic = _topics.DeliveryTopicFor(deliveryId);
        if (topic is null)
        {
            // Refused rather than escaped: see CourierPositionTopic's remarks on why a
            // colon or a star in the id is a namespace-escape, not a formatting problem.
            return Problem(
                title: "deliveryId is not a valid delivery identifier.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // delivery-service is the authority on who is a party — the gateway composes its
        // verdict and never reads another service's store.
        var delivery = await _participants.ResolveAsync(deliveryId, ct);
        if (delivery is null)
        {
            return NotFound();
        }

        // FAIL-CLOSED. Only the two bound parties may watch the courier move. Admins are
        // exempt for ops triage, matching the snapshot route's rule (BR-TRK-1) so the two
        // reads of the same fact cannot disagree about who may see it.
        if (!delivery.IsParty(viewerId) && !UserIdentity.IsAdmin(HttpContext))
        {
            return Problem(
                title: "You are not a party to this delivery.",
                detail: "Only the client who owns this delivery and the jeeber assigned to "
                    + "it may subscribe to its live position.",
                statusCode: StatusCodes.Status403Forbidden,
                type: "https://jeeb.dev/errors/tracking-not-a-party");
        }

        var credential = _guardian.Issue(
            subject: viewerId,
            topic: topic,
            scopes: RealtimeGuardianTokenIssuer.SubscribeOnly);
        if (credential is null)
        {
            // No secret configured. Returning a descriptor without a credential would be
            // worse than 503: the client cannot join with it, and the only thing left
            // that WOULD work is the upstream's open minter — which hands out "*". Fail
            // closed and say why.
            return Problem(
                title: "Realtime credentials are not configured.",
                detail: "Services:Realtime:GuardianSecret is unset, so the gateway cannot "
                    + "issue a delivery-scoped subscribe credential.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Ok(new DeliveryPositionChannelDescriptor
        {
            DeliveryId = deliveryId,
            Topic = topic,
            // Phoenix routes "topic:*" to LiveCommWeb.Channels.TopicChannel; the join
            // payload selects streams. Spelled out so the client does not have to know
            // the service's routing table.
            Channel = "topic:" + topic,
            Stream = CourierPositionTopic.Stream,
            SocketUrl = _realtimeOptions.PublicSocketUrl,
            Token = credential.Token,
            ExpiresAt = credential.ExpiresAt,
        });
    }
}

/// <summary>
/// What a client needs to start receiving positions, and nothing more. The token is
/// scoped to <see cref="Topic"/> with subscribe rights only.
/// </summary>
public sealed class DeliveryPositionChannelDescriptor
{
    /// <summary>The delivery this descriptor is for.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The realtime topic, <c>{tenant}:delivery:{deliveryId}</c>.</summary>
    public required string Topic { get; init; }

    /// <summary>The Phoenix channel to join, <c>topic:{topic}</c>.</summary>
    public required string Channel { get; init; }

    /// <summary>The stream to select in the join payload, <c>location</c>.</summary>
    public required string Stream { get; init; }

    /// <summary>
    /// Device-reachable WebSocket url, or <c>null</c> when the deployment has not
    /// configured one (never a guess derived from a loopback base url).
    /// </summary>
    public string? SocketUrl { get; init; }

    /// <summary>Short-lived, delivery-scoped, subscribe-only Guardian credential.</summary>
    public required string Token { get; init; }

    /// <summary>When <see cref="Token"/> stops being accepted; re-fetch before then.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
