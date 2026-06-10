using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Financials.Cod;
using JeebGateway.Tracking;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// COD-compose BFF surface (S10 H3.3/H4/N10-N12, JEB-56/57/62).
///
/// Thin gateway composition over unified_payment_gateway (UPG). The gateway
/// authorizes the USER (jeeber / admin) JWT at its OWN boundary, then proxies
/// the corresponding UPG route via <see cref="IUnifiedPaymentCodClient"/>:
///
///   * POST /api/v1/payments/cod/record               — record COD intent (party).
///   * GET  /api/v1/payments/cod_jeeb/by-delivery/{id} — read COD record (party/admin).
///   * POST /admin/v1/settlements/{batchId}/mark-paid  — bank-confirmation (admin).
///
/// LAWS honored:
///   * Payments only via UPG — the gateway NEVER touches a provider; it RECORDS
///     an intent / READS / FRONTS the admin action against UPG's live routes.
///   * No inter-service coupling — the gateway composes UPG; it does not read
///     UPG's DB or call any other backend on UPG's behalf.
///   * Identity ids are text (forwarded verbatim).
///
/// UPG's status + body are re-emitted VERBATIM so the upstream contract is never
/// reshaped. When UPG is unreachable, the compose surface returns 502.
/// </summary>
[ApiController]
[Produces("application/json", "application/problem+json")]
public sealed class CodSettlementComposeController : ControllerBase
{
    private readonly IUnifiedPaymentCodClient _upg;
    private readonly ISettlementService _settlements;
    private readonly IDeliveryParticipantResolver _participants;

    public CodSettlementComposeController(
        IUnifiedPaymentCodClient upg,
        ISettlementService settlements,
        IDeliveryParticipantResolver participants)
    {
        _upg = upg;
        _settlements = settlements;
        _participants = participants;
    }

    /// <summary>
    /// POST /api/v1/payments/cod/record — records the COD settlement intent for a
    /// delivery on UPG. The recording Jeeber must be a party to the delivery (or
    /// admin); the amounts are taken from the gateway-side settlement row so the
    /// caller cannot choose the commission (UPG copies them verbatim, BR-16).
    /// </summary>
    [HttpPost("api/v1/payments/cod/record")]
    [RequireCapability(Capabilities.DeliveryParticipate)] // {client, jeeber}; party/admin is STATE in-action
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RecordCod(
        [FromBody] CodRecordBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (body is null || string.IsNullOrWhiteSpace(body.DeliveryId))
            return BadRequest(Problem("cod-record-body-required", "deliveryId is required."));

        // The settlement row is the authoritative amount + party source. A COD
        // record requires the Jeeber to have already settled the cash on the
        // gateway (POST /deliveries/{id}/settle) — that row holds the verbatim
        // commission UPG must copy.
        var settlement = await _settlements.GetByDeliveryAsync(body.DeliveryId, ct);
        if (settlement is null)
            return NotFound();

        var isParty = string.Equals(settlement.JeeberId, userId, StringComparison.Ordinal)
                   || string.Equals(settlement.ClientId, userId, StringComparison.Ordinal);
        if (!isParty && !UserIdentity.IsAdmin(HttpContext))
            return Forbidden();

        var result = await _upg.RecordCodAsync(new CodRecordRequest(
            DeliveryId: settlement.DeliveryId,
            JeeberId: settlement.JeeberId,
            GrossAmount: settlement.GoodsCost,
            CommissionRate: settlement.CommissionRate,
            CommissionAmount: settlement.Commission,
            Currency: settlement.Currency,
            Metadata: new Dictionary<string, string> { ["source"] = "jeeb.cod" }), ct);

        return Passthrough(result);
    }

    /// <summary>
    /// GET /api/v1/payments/cod_jeeb/by-delivery/{deliveryId} — reads the COD
    /// record from UPG, authorized by the USER JWT at the gateway boundary (NOT
    /// the db-probe service key, which UPG's :api pipeline 401s). The caller must
    /// be a party to the delivery (or admin).
    /// </summary>
    [HttpGet("api/v1/payments/cod_jeeb/by-delivery/{deliveryId}")]
    [RequireCapability(Capabilities.DeliveryParticipate)] // {client, jeeber}; party/admin is STATE in-action
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetCodByDelivery(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        // Authorize against the gateway's view of the delivery parties first so a
        // non-party never reaches UPG. The settlement row (if any) is the
        // strongest party source; fall back to the delivery participant resolver.
        var settlement = await _settlements.GetByDeliveryAsync(deliveryId, ct);
        var isParty =
            settlement is not null && (
                string.Equals(settlement.JeeberId, userId, StringComparison.Ordinal)
                || string.Equals(settlement.ClientId, userId, StringComparison.Ordinal));

        if (!isParty && !UserIdentity.IsAdmin(HttpContext))
        {
            var participants = await _participants.ResolveAsync(deliveryId, ct);
            if (participants is null)
                return NotFound();
            if (!participants.IsParty(userId))
                return Forbidden();
        }

        var result = await _upg.GetCodByDeliveryAsync(deliveryId, ct);
        return Passthrough(result);
    }

    /// <summary>
    /// POST /admin/v1/settlements/{batchId}/mark-paid — the bank-confirmation
    /// action. The gateway gates on the admin user-type, then fronts UPG's
    /// AdminAuthPlug route with UPG's admin credential + the authenticated
    /// principal id as X-Admin-Id (paidBy is the principal, never a client header
    /// — closes E12). UPG's status (200 / 409 already-paid / 422 terminal /
    /// 404 unknown) is re-emitted verbatim.
    /// </summary>
    [HttpPost("admin/v1/settlements/{batchId}/mark-paid")]
    [RequireCapability(Capabilities.SettlementsManage)] // {admin}
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> MarkPaid(
        string batchId, [FromBody] object? _, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        if (!UserIdentity.IsAdmin(HttpContext))
            return Forbidden();

        var result = await _upg.MarkBatchPaidAsync(batchId, adminId, ct);
        return Passthrough(result);
    }

    private IActionResult Passthrough(UpgResult result)
    {
        if (!result.Reachable)
            return StatusCode(StatusCodes.Status502BadGateway,
                Problem("upg-unreachable", "unified_payment_gateway could not be reached."));

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            ContentType = result.ContentType,
            Content = result.Body,
        };
    }

    private IActionResult Forbidden() => StatusCode(StatusCodes.Status403Forbidden,
        Problem("settlement-not-a-party", "You are not authorized for this settlement action."));

    private static ProblemDetails Problem(string slug, string title) => new()
    {
        Title = title,
        Status = StatusCodes.Status400BadRequest,
        Type = $"https://jeeb.dev/errors/{slug}",
    };

    /// <summary>POST /api/v1/payments/cod/record body.</summary>
    public sealed class CodRecordBody
    {
        public string? DeliveryId { get; set; }
    }
}
