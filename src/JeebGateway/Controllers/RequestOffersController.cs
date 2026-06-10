using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeber-facing offer submission and management endpoints
/// (T-backend-010 / JEEB-28). Implements the reverse-auction submission
/// half of the lifecycle:
///
/// <list type="bullet">
///   <item><c>POST /requests/{requestId}/offers</c> — submit a bid.</item>
///   <item><c>DELETE /requests/{requestId}/offers/{offerId}</c> — retract
///     a bid before the Client accepts it. The Jeeber may submit again
///     after withdrawing (acceptance criterion).</item>
/// </list>
///
/// Acceptance criteria enforced here:
/// <list type="bullet">
///   <item>Fee &gt;= $1 (mobile floor; DB CHECK enforces &gt; 0).</item>
///   <item>Max <see cref="MaxLiveOffersPerRequest"/> live offers per request.</item>
///   <item>One live offer per Jeeber per request (re-offer allowed after
///     withdraw).</item>
///   <item>Realtime "new offer" event to the Client on every accepted
///     submission (<see cref="IOfferRealtimeNotifier"/>, currently stubbed
///     in-memory).</item>
/// </list>
///
/// Accepting an offer remains on <see cref="OffersController"/> at
/// <c>POST /offers/{offerId}/accept</c> because that path is keyed by
/// offer-id, not request-id.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
public class RequestOffersController : ControllerBase
{
    /// <summary>
    /// T-backend-010 acceptance criterion: at most 20 live offers per
    /// request. Hard ceiling at the application layer — beyond this the
    /// auction is effectively saturated and additional bids do not help
    /// the Client compare.
    /// </summary>
    public const int MaxLiveOffersPerRequest = 20;

    /// <summary>
    /// Minimum gross fee in the Client's currency. Below $1 is treated
    /// as a fat-finger / abuse signal — the DB CHECK separately enforces
    /// <c>fee &gt; 0</c>.
    /// </summary>
    public const decimal MinimumFee = 1m;

    /// <summary>
    /// Mirrors the DB CHECK <c>offers_note_length</c> in 0007. Caught at
    /// the controller so a bad note never reaches the store.
    /// </summary>
    public const int MaxNoteLength = 500;

    private readonly IPendingOffersStore _offers;
    private readonly IRequestsStore _requests;
    private readonly IDualRoleService _dualRole;
    private readonly IOfferRealtimeNotifier _realtime;
    private readonly IOfferRequestIndex _offerRequestIndex;
    private readonly IJeebConversationClient _conversations;
    private readonly UpstreamFeatureFlags _flags;
    private readonly TimeProvider _clock;
    private readonly ILogger<RequestOffersController> _logger;

    public RequestOffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        IDualRoleService dualRole,
        IOfferRealtimeNotifier realtime,
        IOfferRequestIndex offerRequestIndex,
        IJeebConversationClient conversations,
        IOptions<UpstreamFeatureFlags> flags,
        TimeProvider clock,
        ILogger<RequestOffersController> logger)
    {
        _offers = offers;
        _requests = requests;
        _dualRole = dualRole;
        _realtime = realtime;
        _offerRequestIndex = offerRequestIndex;
        _conversations = conversations;
        _flags = flags.Value;
        _clock = clock;
        _logger = logger;
    }

    [HttpPost("requests/{requestId}/offers")]
    // ADR-005 L2 §D jeeber-only: replaces [RequireRole(Roles.Jeeber)]. Fee/cap/one-live-offer = STATE.
    [RequireCapability(Capabilities.OfferSubmit)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(OfferDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(
        string requestId,
        [FromBody] CreateOfferBody? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Fee is null || body.Fee < MinimumFee)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"fee must be at least ${MinimumFee:0.##}.",
                Detail = body.Fee is null ? "fee is missing." : $"received={body.Fee.Value}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/offer-fee-too-low"
            });
        }

        if (body.EtaMinutes is null || body.EtaMinutes.Value <= 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "etaMinutes must be a positive integer.",
                Detail = body.EtaMinutes is null ? "etaMinutes is missing." : $"received={body.EtaMinutes.Value}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/offer-eta-invalid"
            });
        }

        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is not null && note.Length > MaxNoteLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"note exceeds {MaxNoteLength} characters.",
                Detail = $"length={note.Length}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/offer-note-too-long"
            });
        }

        var request = await _requests.GetAsync(requestId, ct);
        if (request is null) return NotFound();

        // BR-1 (T-backend-041): a user cannot act as both Client and Jeeber
        // on the same delivery. Block at submission time so the Client side
        // never sees a self-offer notification.
        if (await _dualRole.WouldViolateSameDeliveryRuleAsync(jeeberId, requestId, ct))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot offer on your own delivery request (BR-1).",
                Detail = "A user cannot act as both Client and Jeeber on the same delivery.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/same-delivery-role-violation"
            });
        }

        // Offers only make sense while the auction is open — once an offer
        // is accepted (or the request is cancelled / expired) further bids
        // are noise. Mirrors the offer-service's own gating.
        if (!RequestStatus.IsPreAcceptance(request.Status))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Request is no longer accepting offers.",
                Detail = $"Current status: {request.Status}.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/request-not-open-for-offers"
            });
        }

        PendingOffer created;
        try
        {
            created = await _offers.TrySubmitAsync(
                requestId,
                jeeberId,
                body.Fee.Value,
                body.EtaMinutes.Value,
                note,
                MaxLiveOffersPerRequest,
                _clock.GetUtcNow(),
                ct,
                // GW-1: the request creator's id. The upstream-backed store uses
                // it to mirror the request into offer-service (OS-1) and retry
                // when the submit 404s because the row was never mirrored. The
                // in-memory store ignores it.
                clientId: request.ClientId);
        }
        catch (OfferUpstreamValidationException ex)
        {
            // GW-2: offer-service rejected the mirror/submit payload (422/400).
            // A caller-correctable validation failure — surface a 422
            // ProblemDetails, never the global handler's opaque 502.
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "offer-service rejected the offer payload.",
                Detail = $"stage={ex.Stage}; upstreamStatus={ex.UpstreamStatus}; code={ex.UpstreamCode ?? "validation_error"}",
                Status = StatusCodes.Status422UnprocessableEntity,
                Type = "https://jeeb.dev/errors/offer-upstream-validation"
            });
        }
        catch (DuplicateOfferException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "You already have a live offer on this request.",
                Detail = $"Withdraw offer {ex.ExistingOfferId} before submitting a new bid.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offer-already-exists"
            });
        }
        catch (TooManyOffersForRequestException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = $"Maximum {ex.Limit} offers per request reached.",
                Detail = $"Live offers: {ex.LiveCount}.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offers-per-request-exceeded"
            });
        }

        // Record the offerId → (requestId, jeeberId) routing pairing so the
        // offer-scoped accept route (POST /offers/{offerId}/accept) can (a)
        // forward to the request-scoped offer-service accept saga and (b) detect
        // a genuine BR-1 self-offer (accepting CLIENT == this offer's bidder)
        // without an extra round-trip. This is a thin BFF routing concern, not
        // auction state (see IOfferRequestIndex).
        _offerRequestIndex.Record(created.Id, requestId, created.JeeberId);

        // S08 (B) — SEAT THE OFFERING JEEBER AS A CONVERSATION PARTICIPANT.
        // The conversation aggregate is created at order-create seating ONLY the
        // client owner; the offer jeebers are never added, so H5/N3/N4/A6 correctly
        // 403 for them until they are seated. When a jeeber submits an offer on the
        // request we add them to the request's conversation as `jeeber_offerer` so
        // they (and only they + the client) can read the conversation (200) while a
        // true non-member still 403s. The gateway is the SOLE chat caller (org
        // no-coupling law) and computes NO membership — it forwards
        // (conversationId, jeeberId, role) to chat-service, the membership authority.
        //
        // DEGRADE-DON'T-FAIL: the offer is already durable and the 201 is committed.
        // A chat blip, a disabled Chat flag, or a request row that never got a
        // conversation id (chat was down at create) must NEVER turn the offer 201
        // into a 5xx — every failure is logged and swallowed, exactly like the
        // realtime fan-out below.
        if (_flags.Chat)
        {
            await SeatOfferingJeeberAsync(request, created, requestId, ct);
        }

        // Realtime fan-out is best-effort: the offer is already durable, so
        // a notifier failure must not flip the 201 into a 5xx. The Client
        // can also poll the offer-listing endpoint if the WS event is lost.
        try
        {
            await _realtime.NotifyNewOfferAsync(request.ClientId, created, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to dispatch realtime new-offer event for request {RequestId}, offer {OfferId}",
                requestId, created.Id);
        }

        return Created($"/requests/{requestId}/offers/{created.Id}", ToDto(created));
    }

    [HttpDelete("requests/{requestId}/offers/{offerId}")]
    // ADR-005 L2 §D jeeber-only (STATE: ownership of the offer stays in-action/owning service).
    [RequireCapability(Capabilities.OfferWithdraw)]
    [RequireActiveUser]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(string requestId, string offerId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var unauthorized)) return unauthorized;

        var outcome = await _offers.TryWithdrawAsync(
            offerId, requestId, jeeberId, _clock.GetUtcNow(), ct);

        return outcome switch
        {
            WithdrawOfferOutcome.Withdrawn => NoContent(),
            WithdrawOfferOutcome.NotFound => NotFound(),
            WithdrawOfferOutcome.NotOwned => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Only the Jeeber who submitted the offer may withdraw it.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/offer-not-owned"
            }),
            WithdrawOfferOutcome.NotPending => Conflict(new ProblemDetails
            {
                Title = "Offer can no longer be withdrawn.",
                Detail = "The offer has already been accepted or previously withdrawn.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offer-not-pending"
            }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// S08 (B) — seat the offering jeeber as a <c>jeeber_offerer</c> participant on the
    /// request's conversation so they (and only they + the client) read the conversation
    /// 200 while a true non-member still 403s. chat-service is the membership authority;
    /// the gateway forwards (conversationId, jeeberId, role) and computes NO membership.
    ///
    /// <para>CONVERSATION-ID RESOLUTION (the H5/N3/N4/A6 fix). The conversation can be
    /// created down two paths that do not both stamp the id onto the gateway's request
    /// ledger row: (1) order-create auto-create
    /// (<c>DurableRequestsStore.CreateBroadcastingConversationAsync</c>) writes
    /// <c>request.ConversationId</c>; (2) the client's explicit
    /// <c>POST /v1/chat/jeeb/conversations</c> creates the conversation in chat-service
    /// keyed by <c>correlation_key == requestId</c> but does NOT write the id back onto
    /// the request row. In case (2) <c>request.ConversationId</c> is empty at offer-submit,
    /// so the previous guard skipped the seat and the seated-but-unseated jeeber 403'd.
    /// We therefore fall back to resolving the conversation by its correlation key (==
    /// requestId) via chat-service — the membership authority and the SOLE owner of the
    /// conversation-by-correlation lookup — before seating. The gateway holds no
    /// conversation state; it only composes the read+seat.</para>
    ///
    /// <para>DEGRADE-DON'T-FAIL: every failure (chat blip, no conversation yet, lookup
    /// 404) is logged and swallowed — the offer is already durable and the 201 is
    /// committed, so a seat failure must NEVER flip it to a 5xx. A jeeber that could not
    /// be seated simply reads 403 until reconciled, exactly as before.</para>
    /// </summary>
    private async Task SeatOfferingJeeberAsync(
        DeliveryRequest request, PendingOffer created, string requestId, CancellationToken ct)
    {
        try
        {
            // Prefer the id already stamped on the ledger row (order-create auto-create
            // path); otherwise resolve it from chat-service by correlation key (==
            // requestId) for the client-created-conversation path. chat-service owns the
            // by-correlation lookup; the gateway does not derive the id itself.
            var conversationId = request.ConversationId;
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                var conversation = await _conversations.GetConversationByCorrelationAsync(requestId, ct);
                conversationId = conversation?.ConversationId;
            }

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                _logger.LogInformation(
                    "Offer {OfferId} on request {RequestId}: no conversation resolvable on the ledger row "
                    + "or by correlation key; skipping jeeber-seat (chat was unavailable at create, "
                    + "auto-create is off, and no conversation was created for the request yet).",
                    created.Id, requestId);
                return;
            }

            await _conversations.AddParticipantAsync(
                conversationId,
                new AddJeebParticipantRequest
                {
                    UserId = created.JeeberId,
                    // JEB-1488 (correction #1 / GR2): seat the offering jeeber under the
                    // GENERIC permission tag the gateway maps the Jeeb role onto — the
                    // Jeeb role name never crosses to the shared chat-service.
                    RoleInConvo = ConversationParticipantTag.FromJeebRole(
                        ConversationParticipantTag.JeebOffererRole),
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to seat offering jeeber {JeeberId} on the conversation for request {RequestId}; "
                + "offer stays 201, the jeeber will read 403 until reconciled.",
                created.JeeberId, requestId);
        }
    }

    private static OfferDto ToDto(PendingOffer o) => new()
    {
        Id = o.Id,
        RequestId = o.RequestId,
        JeeberId = o.JeeberId,
        Status = o.Status,
        Fee = o.Fee,
        EtaMinutes = o.EtaMinutes,
        Note = o.Note,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt
    };
}
