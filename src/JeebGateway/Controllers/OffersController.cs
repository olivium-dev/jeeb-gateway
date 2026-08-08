using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Financials;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Offer MUTATION surfaces owned by the legacy controller: the jeeber's own-offer
/// EDIT (<c>PUT /v1/offers/{offerId}</c>) and the request-owning client's single-bid
/// REJECT (<c>POST /v1/offers/{offerId}/reject</c>). Both are thin forwards: the
/// offer-service owns every rule and transition, and the gateway re-emits the
/// upstream status verbatim.
///
/// ACCEPT IS NOT HERE ANY MORE (owner ruling, 2026-08-01). This class used to also
/// carry <c>POST /offers/{offerId}/accept</c>, a duplicate of the V1 route
/// <c>POST /v1/offers/{id}/accept</c> on
/// <see cref="JeebGateway.Controllers.V1.JeebOffersController"/>. It was reachable
/// but dead: every executable accept call site in jeeb-mobile targets the V1 route
/// (jeeb-admin and jeeb-partner-portal do not call accept at all), and the live MSI
/// journal attributed 100% of real accepts to V1 — 27 <c>POST
/// /api/conversations/{id}/settle</c> calls, which only the V1 path makes, against
/// 0 <c>POST /api/members</c> calls, which only the retired path made.
///
/// The retired leg was also REDUNDANT, not merely unused: its post-accept
/// orchestration called <c>IConversationProvisioner.AdvanceToAcceptedAsync</c> to
/// promote the winner and drop losers via the CHANNELS subsystem, while the very
/// same method then called <c>IJeebConversationClient.AdvancePhaseAsync</c> with
/// <c>WinnerUserId</c> + <c>RemoveOthers=true</c> — winner promotion and loser
/// removal, atomically, on the correct aggregate. The channels leg's return value
/// was discarded, and its first step (<c>POST /api/members</c>) is NOT
/// channel-scoped, so had it ever fired it would have SUCCEEDED and minted an
/// orphan chat member row before its channel-scoped second step failed.
///
/// Do NOT re-add an accept action here. There is exactly one accept surface now,
/// and a second one is how the two paths diverged in the first place — the V1 route
/// had to back-port THIS route's BR-1 self-offer guard and its deterministic
/// Idempotency-Key to stop them drifting (JeebOffersController F5/F6).
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
// Retained deliberately although BOTH surviving actions declare ABSOLUTE route
// templates ("/v1/..."), which override a controller-level prefix. It is the last
// marker that the "offers" prefix belonged to this controller; removing it changes
// no route. The one action that consumed it (the retired accept) is gone.
[Route("offers")]
public class OffersController : ControllerBase
{
    /// <summary>
    /// JEB-1474 — the Jeeb offer edit cap. This is a PRODUCT policy owned by the
    /// gateway, not the shared offer-service. It is forwarded as <c>max_edits</c>
    /// so offer-service enforces the ceiling without hardcoding the literal "2".
    /// </summary>
    public const int OfferEditCap = 2;

    private readonly IOfferServiceClient _offerService;
    private readonly IOfferRequestIndex _offerRequestIndex;
    private readonly IWalletSufficiencyGuard _walletGuard;
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<OffersController> _logger;

    public OffersController(
        IOfferServiceClient offerService,
        IOfferRequestIndex offerRequestIndex,
        IWalletSufficiencyGuard walletGuard,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<OffersController> logger)
    {
        _offerService = offerService;
        _offerRequestIndex = offerRequestIndex;
        _walletGuard = walletGuard;
        _flags = flags.Value;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // S08 A3 — offer EDIT (jeeber edits their own pending bid).
    // -------------------------------------------------------------------------

    /// <summary>
    /// S08 A3 — a JEEBER edits their own pending offer (fee / eta / note). The
    /// mobile route is offer-scoped (<c>PUT /v1/offers/{offerId}</c>) while the
    /// canonical offer-service edit route is request-scoped
    /// (<c>PUT /api/v1/requests/{requestId}/offers/{offerId}</c>), so the gateway
    /// resolves the requestId from its routing index (learned at submit) and
    /// forwards the actor as <c>x-user-id</c>. offer-service owns the edit rule
    /// (only the owning jeeber, ≤ 2 edits, only while submitted/edited) and the
    /// <c>edited</c> transition; the gateway re-derives nothing and forwards the
    /// upstream status verbatim. ProblemDetails on every negative (RFC 7807).
    /// </summary>
    [HttpPut("/v1/offers/{offerId}")]
    [RequireCapability(Capabilities.OfferEditOwn)] // {jeeber}; ownership (offer.jeeber_id == actor) = STATE (offer-service)
    [RequireActiveUser]
    [ProducesResponseType(typeof(OfferWire), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Edit(
        string offerId, [FromBody] EditOfferBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var actorId, out var problem)) return problem;

        if (body is null || (body.Fee is null && body.EtaMinutes is null && body.Note is null))
        {
            return Problem(
                title: "At least one of fee, etaMinutes, or note is required to edit an offer.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Kill-switch OFF → 503. There is no second edit path to fall back to: GW3
        // deleted the gateway's in-memory offer store, so offer-service owns the edit
        // rule and the 2-edit cap (JEB-1474) unconditionally. The comment that used to
        // sit here claimed "the gateway IS the offer record-of-truth when the Offer
        // kill-switch is off, so the 2-edit cap is enforced here against the in-memory
        // store" — untrue since GW3, and untrue of this branch even before it, which
        // has returned 503 rather than calling the local helper.
        if (!_flags.Offer)
        {
            return OfferUpstreamUnavailable("edit");
        }

        var requestId = _offerRequestIndex.ResolveRequestId(offerId);
        if (requestId is null)
        {
            // Unknown to this gateway instance (never submitted through here / index
            // lost on restart). 404 is the correct contract for a phantom offer.
            _logger.LogInformation(
                "Edit for offer {OfferId} could not resolve a requestId from the routing index; returning 404.",
                offerId);
            return NotFound();
        }

        // Dollars → cents on the wire (offer-service is cents-based, mirroring submit).
        long? feeCents = body.Fee is decimal fee ? (long)Math.Round(fee * 100m) : null;

        // F1 guard 3 (API-only hardening — mobile never calls this route). Only a RAISED
        // fee needs a re-check; a lowered/unchanged fee needs no more balance than before.
        if (feeCents is long newFeeCents && Guid.TryParse(actorId, out var jeeberGuid))
        {
            var currentFeeCents = await ResolveCurrentFeeCentsAsync(actorId, offerId, ct);
            if (currentFeeCents is long cur && newFeeCents > cur)
            {
                var required = WalletGuardContract.RequiredCommission(newFeeCents / 100m);
                var guard = await _walletGuard.CheckAsync(jeeberGuid, required, ct);
                if (!guard.Allowed)
                {
                    if (guard.DegradedByUpstreamFailure)
                    {
                        return StatusCode(StatusCodes.Status503ServiceUnavailable,
                            WalletGuardContract.WalletUnavailableProblem());
                    }

                    return StatusCode(StatusCodes.Status402PaymentRequired, new ProblemDetails
                    {
                        Title = "Wallet balance does not cover the raised offer's commission.",
                        Status = StatusCodes.Status402PaymentRequired,
                        Type = "https://jeeb.dev/errors/insufficient-wallet-balance",
                        Extensions =
                        {
                            ["needed"] = guard.Required,
                            ["available"] = guard.Available,
                            ["currency"] = guard.Currency,
                        }
                    });
                }
            }
        }

        OfferMutationResult result;
        try
        {
            result = await _offerService.EditAsync(
                actorId, requestId, offerId, feeCents, body.EtaMinutes, body.Note, OfferEditCap, ct);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "offer-service edit for offer {OfferId} failed.", offerId);
            return OfferUpstreamUnavailable("edit");
        }

        return MapMutation(result, "edit");
    }

    /// <summary>F1 guard 3: the offer's current fee, read via the jeeber-scoped feed list
    /// (the actor here IS the jeeber). Null when unresolvable — the guard then skips.</summary>
    private async Task<long?> ResolveCurrentFeeCentsAsync(string jeeberId, string offerId, CancellationToken ct)
    {
        var offers = await _offerService.ListOffersForJeeberAsync(jeeberId, status: null, ct);
        return offers.FirstOrDefault(o => string.Equals(o.OfferId, offerId, StringComparison.Ordinal))?.FeeCents;
    }

    // GW3 follow-up (2026-08-01): the flag-OFF in-memory offer edit helper
    // (EditInMemoryAsync) and its response projector (ToOfferWire) were DELETED here.
    //
    // EditInMemoryAsync was already UNREACHABLE before this change: its only would-be
    // caller, Edit() above, returns OfferUpstreamUnavailable("edit") on the !_flags.Offer
    // branch and never called it. It read and mutated offers through IPendingOffersStore,
    // whose sole remaining implementation (UpstreamPendingOffersStore) throws
    // NotSupportedException from both GetAsync and TryEditAsync — so even if something had
    // reached it, it could not have edited anything. ToOfferWire had exactly one call site,
    // inside that dead method.
    //
    // Do NOT reinstate a local edit as a "fallback". offer-service owns the edit rule and
    // the 2-edit cap; a second implementation of a capped mutation is how edit-count drift
    // and lost-update races get introduced.

    // -------------------------------------------------------------------------
    // S08 A5 — offer REJECT (request-owning client declines one bid).
    // -------------------------------------------------------------------------

    /// <summary>
    /// S08 A5 — the request-owning CLIENT rejects a single jeeber's bid (distinct
    /// from the accept-saga's automatic sibling rejection). The route is offer-scoped
    /// (<c>POST /v1/offers/{offerId}/reject</c>), mirroring the offer-scoped reject
    /// route added to offer-service; the gateway forwards the actor as
    /// <c>x-user-id</c> and the upstream status verbatim. offer-service owns the
    /// reject rule (only the request's client may reject; submitted/edited → rejected
    /// with an already-rejected guard) and the transition. The gateway re-derives
    /// nothing. ProblemDetails on every negative (RFC 7807).
    /// </summary>
    [HttpPost("/v1/offers/{offerId}/reject")]
    [RequireCapability(Capabilities.OfferReject)] // {client}; authz (request.client_id == actor) = STATE (offer-service)
    [RequireActiveUser]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Reject(string offerId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var actorId, out var problem)) return problem;

        // Reject is an UPSTREAM-only surface (no legacy in-memory reject path). With
        // the offer kill-switch off the gateway is not the offer record-of-truth.
        if (!_flags.Offer)
        {
            return OfferUpstreamUnavailable("reject");
        }

        OfferMutationResult result;
        try
        {
            result = await _offerService.RejectAsync(actorId, offerId, ct);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "offer-service reject for offer {OfferId} failed.", offerId);
            return OfferUpstreamUnavailable("reject");
        }

        return MapMutation(result, "reject");
    }

    /// <summary>
    /// Maps a status-preserving <see cref="OfferMutationResult"/> onto the caller
    /// response: 200 (with the edit projection when present) or the matching negative
    /// ProblemDetails. The gateway re-derives no rule — it forwards the upstream
    /// outcome verbatim.
    /// </summary>
    private IActionResult MapMutation(OfferMutationResult result, string action) => result.Status switch
    {
        OfferMutationStatus.Ok => result.Offer is not null ? Ok(result.Offer) : Ok(),

        OfferMutationStatus.NotOwner => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Title = action == "reject"
                ? "Only the request owner can reject an offer."
                : "Only the offer's owner can edit it.",
            Status = StatusCodes.Status403Forbidden,
            Type = "https://jeeb.dev/errors/offer-not-owned"
        }),

        OfferMutationStatus.Conflict => Conflict(new ProblemDetails
        {
            Title = action == "reject"
                ? "Offer can no longer be rejected."
                : "Offer can no longer be edited.",
            Detail = action == "reject"
                ? "The offer was already rejected, accepted, or withdrawn."
                : "The offer is no longer pending or has reached its edit limit.",
            Status = StatusCodes.Status409Conflict,
            Type = "https://jeeb.dev/errors/offer-not-pending"
        }),

        // NotFound (and any unmapped status) → 404 phantom offer.
        _ => NotFound()
    };

    /// <summary>
    /// 503 kill-switch ProblemDetails for the upstream-only offer mutation surfaces
    /// (edit / reject) — mirrors the conversation BFF's UpstreamUnavailable shape.
    /// </summary>
    private ObjectResult OfferUpstreamUnavailable(string action) => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Title = $"The offer {action} surface is not available.",
            Detail = "offer-service is not wired (FeatureFlags:UseUpstream:Offer is off) "
                + "or is unreachable; the gateway holds no offer record-of-truth of its own.",
            Status = StatusCodes.Status503ServiceUnavailable,
        });
}
