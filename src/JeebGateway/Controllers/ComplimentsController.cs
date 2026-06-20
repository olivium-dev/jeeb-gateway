using JeebGateway.Auth.Capabilities;
using JeebGateway.Services;
using JeebGateway.Services.Generated.ComplimentService;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Gap 3 — the thin compliment BFF (<c>/api/compliments/*</c>) over the shared
/// <c>compliment-service</c> (partner-to-partner compliment aggregate;
/// olivium-shared, the same upstream rahmah-gateway consumes via its
/// <c>ServiceComplimentClient</c>), reached through
/// <see cref="IComplimentServiceClient"/>. The gateway holds NO compliment
/// state and contains NO domain logic — it only (a) resolves the acting caller
/// from identity and (b) forwards to the upstream client. Every read/write
/// resolves to the upstream's store.
///
/// <para><b>Sender provenance.</b> The acting caller (the identified user) is
/// ALWAYS <see cref="ComplimentCreate.PartnerId1"/> — never taken from the
/// request body. The body supplies only the recipient and the message. This is
/// the one invariant the BFF enforces; everything else (validation, dedup,
/// persistence) belongs to compliment-service.</para>
///
/// <para><b>Identity.</b> L1 authentication is preserved imperatively via
/// <see cref="UserIdentity.TryGetUserId"/> in each action (JWT subject, falling
/// back to the edge-injected <c>X-User-Id</c> header for the MVP), so an
/// anonymous caller gets 401. <see cref="PublicEndpointAttribute"/> opts the
/// controller out of the L2 capability scan (ADR-005 §A): the compliment surface
/// is caller-scoped and authorized in-action by identity, not by a coarse
/// user-type capability. This mirrors the KycBff / KycSubmissionBff
/// header-identity precedent exactly.</para>
///
/// <para><b>Kill switch.</b> Gated by <c>FeatureFlags:UseUpstream:Compliment</c>.
/// This is a NET-NEW path (the gateway never held a compliment store), so the
/// flag is a runtime kill switch, NOT a fallback to local state: when off the
/// endpoints return 503 ProblemDetails — checked BEFORE identity so the surface
/// is dark by default regardless of caller. Defaults OFF in EVERY environment;
/// flip on once compliment-service is on the Jeeb swarm and
/// <c>ComplimentServiceApi:BaseUrl</c> is a real host. Mirrors the cdn-service /
/// contract-signing-service net-new kill-switch shape exactly.</para>
/// </summary>
[ApiController]
[Route("api/compliments")]
[PublicEndpoint("Caller-scoped compliment brokering — L2-public per ADR-005 §A; L1 auth preserved in-action via UserIdentity.")]
public sealed class ComplimentsController : ControllerBase
{
    private readonly IComplimentServiceClient _compliments;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public ComplimentsController(
        IComplimentServiceClient compliments,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _compliments = compliments;
        _flags = flags;
    }

    /// <summary>
    /// Sends a compliment from the authenticated caller to a recipient. The caller
    /// is stamped as <c>partner_id_1</c> (sender); the body supplies only the
    /// recipient and message. Real upstream path: <c>POST /api/v1/compliments/</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ComplimentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] CreateComplimentRequest? request, CancellationToken ct = default)
    {
        if (!_flags.CurrentValue.Compliment) return UpstreamDisabled();
        if (!UserIdentity.TryGetUserId(HttpContext, out var senderId, out var unauthorized)) return unauthorized;

        if (request is null || string.IsNullOrWhiteSpace(request.RecipientId))
        {
            return Problem(
                title: "Invalid compliment",
                detail: "A non-empty 'recipientId' is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Problem(
                title: "Invalid compliment",
                detail: "A non-empty 'message' is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var response = await _compliments.CreateComplimentAsync(
            new ComplimentCreate
            {
                // The sender is ALWAYS the authenticated caller, never the body.
                PartnerId1 = senderId,
                PartnerId2 = request.RecipientId,
                Message = request.Message,
            },
            ct);

        return Ok(response);
    }

    /// <summary>
    /// The set of partner ids the authenticated caller has exchanged compliments
    /// with. Real upstream path:
    /// <c>GET /api/v1/compliments/connections/{user_id}</c>.
    /// </summary>
    [HttpGet("connections")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Connections(CancellationToken ct = default)
    {
        if (!_flags.CurrentValue.Compliment) return UpstreamDisabled();
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        var connections = await _compliments.GetUserConnectionsAsync(userId, ct);
        return Ok(connections);
    }

    /// <summary>
    /// The sender ids of compliments received by the authenticated caller. Real
    /// upstream path: <c>GET /api/v1/compliments/received/{user_id}</c>.
    /// </summary>
    [HttpGet("received")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Received(CancellationToken ct = default)
    {
        if (!_flags.CurrentValue.Compliment) return UpstreamDisabled();
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        var received = await _compliments.GetReceivedComplimentsAsync(userId, ct);
        return Ok(received);
    }

    /// <summary>
    /// The full compliment conversation between the authenticated caller
    /// (<c>user_id_1</c>) and a counterpart (<c>user_id_2</c>, supplied as
    /// <c>fromUserId</c>). Real upstream path:
    /// <c>GET /api/v1/compliments/conversation/{user_id_1}/{user_id_2}</c>.
    /// </summary>
    [HttpGet("conversation")]
    [ProducesResponseType(typeof(ConversationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Conversation([FromQuery] string? fromUserId, CancellationToken ct = default)
    {
        if (!_flags.CurrentValue.Compliment) return UpstreamDisabled();
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (string.IsNullOrWhiteSpace(fromUserId))
        {
            return Problem(
                title: "Invalid conversation request",
                detail: "A non-empty 'fromUserId' query parameter is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var conversation = await _compliments.GetConversationAsync(userId, fromUserId, ct);
        return Ok(conversation);
    }

    private IActionResult UpstreamDisabled() => Problem(
        title: "Compliment upstream disabled",
        detail: "FeatureFlags:UseUpstream:Compliment is off in this environment "
              + "(compliment-service is not yet wired into the Jeeb swarm).",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// BFF-facing create request. The sender is NOT part of the body — it is
    /// derived from the authenticated caller — so this carries only the recipient
    /// and the message text.
    /// </summary>
    public sealed class CreateComplimentRequest
    {
        public string? RecipientId { get; set; }
        public string? Message { get; set; }
    }
}
