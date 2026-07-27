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
/// The gateway authorizes the USER (jeeber / admin) JWT at its OWN boundary, then
/// serves the corresponding route from <see cref="ICodSettlementLedger"/>:
///
///   * POST /api/v1/payments/cod/record               — record COD intent (party).
///   * GET  /api/v1/payments/cod_jeeb/by-delivery/{id} — read COD record (party/admin).
///   * POST /admin/v1/settlements/{batchId}/mark-paid  — bank-confirmation (admin).
///
/// OWNER RULING 2026-07-27 — "jeeb is only cash on delivery": these three routes
/// were previously a thin composition over unified_payment_gateway. UPG is gone;
/// the ledger is now in-process (InProcessCodSettlementLedger). The ROUTES,
/// status codes and body shapes are unchanged — this is a change of WHERE the
/// COD record lives, never of WHETHER it is written. Authorization is unchanged
/// too: a non-party still never reaches the ledger.
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
    private readonly ICodSettlementLedger _ledger;
    private readonly ISettlementService _settlements;
    private readonly IDeliveryParticipantResolver _participants;

    public CodSettlementComposeController(
        ICodSettlementLedger ledger,
        ISettlementService settlements,
        IDeliveryParticipantResolver participants)
    {
        _ledger = ledger;
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

        var result = await _ledger.RecordCodAsync(new CodRecordRequest(
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

        var result = await _ledger.GetCodByDeliveryAsync(deliveryId, ct);
        return Passthrough(result);
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> MarkPaid(
        string batchId, [FromBody] object? _, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;
        if (!UserIdentity.IsAdmin(HttpContext))
            return Forbidden();

        var result = await _ledger.MarkBatchPaidAsync(batchId, adminId, ct);
        return Passthrough(result);
    }

    private IActionResult Passthrough(CodLedgerResult result)
    {
        // Defensive only — the in-process ledger is always available. Retained so
        // the mapping stays total if the ledger is ever backed by a durable store.
        if (!result.Available)
            return StatusCode(StatusCodes.Status502BadGateway,
                Problem("cod-ledger-unavailable", "The COD settlement ledger could not be reached."));

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
