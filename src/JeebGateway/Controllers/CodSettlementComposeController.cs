using JeebGateway.Auth.Capabilities;
using System.Globalization;
using JeebGateway.Financials;
using JeebGateway.Tracking;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// COD-compose BFF surface (S10 H3.3/H4/N10-N12, JEB-56/57/62).
///
/// The gateway authorizes the USER (jeeber / admin) JWT at its OWN boundary, then
/// serves the corresponding route from the settlement row:
///
///   * POST /api/v1/payments/cod/record               — record COD intent (party).
///   * GET  /api/v1/payments/cod_jeeb/by-delivery/{id} — read COD record (party/admin).
///   * POST /admin/v1/settlements/{batchId}/mark-paid  — bank-confirmation (admin).
///
/// gwdbx W2-R11: the settlement row IS the COD record — the in-process ledger was a shadow copy
/// of it and is deleted. The ROUTES, status codes and body shapes are unchanged. mark-paid needs
/// the settlement-service ADMIN scope the gateway does not hold, so it now fails closed.
/// Authorization is unchanged: a non-party still never reaches the record.
///
/// LAWS honored:
///   * The gateway NEVER touches a payment provider — under cash-on-delivery
///     there is no provider; the cash moved hand-to-hand and this records it.
///   * Identity ids are text (forwarded verbatim).
/// </summary>
[ApiController]
[Produces("application/json", "application/problem+json")]
public sealed class CodSettlementComposeController : ControllerBase
{
    private readonly ISettlementService _settlements;
    private readonly IDeliveryParticipantResolver _participants;

    public CodSettlementComposeController(
        ISettlementService settlements,
        IDeliveryParticipantResolver participants)
    {
        _settlements = settlements;
        _participants = participants;
    }

    /// <summary>
    /// POST /api/v1/payments/cod/record — records the COD settlement intent for a
    /// delivery. The recording Jeeber must be a party to the delivery (or admin);
    /// the amounts are taken from the gateway-side settlement row so the caller
    /// cannot choose the commission (copied verbatim, BR-16).
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
        // commission the COD record must copy.
        var settlement = await _settlements.GetByDeliveryAsync(body.DeliveryId, ct);
        if (settlement is null)
            return NotFound();

        var isParty = string.Equals(settlement.JeeberId, userId, StringComparison.Ordinal)
                   || string.Equals(settlement.ClientId, userId, StringComparison.Ordinal);
        if (!isParty && !UserIdentity.IsAdmin(HttpContext))
            return Forbidden();

        // The settlement row already carries the verbatim commission (BR-16); recording the COD
        // is reading it back, not writing a second copy.
        return StatusCode(StatusCodes.Status201Created, new { data = CodRecord(settlement) });
    }

    /// <summary>
    /// GET /api/v1/payments/cod_jeeb/by-delivery/{deliveryId} — reads the COD
    /// record, authorized by the USER JWT at the gateway boundary. The caller must
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
        // non-party never reaches the ledger. The settlement row (if any) is the
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

        return settlement is null ? NotFound(new { error = "not_found" }) : Ok(CodRecord(settlement));
    }

    /// <summary>
    /// POST /admin/v1/settlements/{batchId}/mark-paid — the bank-confirmation
    /// action. The gateway gates on the admin user-type, then marks the batch with
    /// the authenticated principal id as paidBy (never a client-supplied header —
    /// closes E12). The ledger's status (200 / 409 already-paid / 422 terminal /
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult MarkPaid(string batchId, [FromBody] object? _)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var _unused, out var unauthorized)) return unauthorized;
        if (!UserIdentity.IsAdmin(HttpContext))
            return Forbidden();

        // gwdbx W2-R11: paying a batch is an ADMIN-scope settlement-service operation. The gateway
        // holds the SERVICE scope only, so a leaked gateway token cannot pay anyone.
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
        {
            Title = "Settlement payout is served by settlement-service.",
            Detail = "The gateway holds the settlement-service SERVICE scope only; mark-paid "
                     + "requires the ADMIN scope and is not proxied.",
            Status = StatusCodes.Status503ServiceUnavailable,
            Type = SettlementAdminScopeException.ProblemType,
        });
    }

    /// <summary>The COD record wire shape, projected from the settlement row (keys unchanged).</summary>
    private static object CodRecord(Settlement row) => new
    {
        delivery_id = row.DeliveryId,
        provider_id = row.JeeberId,
        jeeber_id = row.JeeberId,
        gross_amount = row.GoodsCost.ToString(CultureInfo.InvariantCulture),
        commission_amount = row.Commission.ToString(CultureInfo.InvariantCulture),
        currency = row.Currency,
        payment_method = row.PaymentMethod,
        status = row.CodState,
        batchId = row.BatchId?.ToString("D"),
    };

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
