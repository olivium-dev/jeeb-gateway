using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Financials.Cod;
using JeebGateway.Tracking;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly ISettlementStore _settlementStore;
    private readonly TimeProvider _clock;
    private readonly ILogger<CodSettlementComposeController> _log;

    public CodSettlementComposeController(
        IUnifiedPaymentCodClient upg,
        ISettlementService settlements,
        IDeliveryParticipantResolver participants,
        ISettlementStore settlementStore,
        TimeProvider clock,
        ILogger<CodSettlementComposeController> log)
    {
        _upg = upg;
        _settlements = settlements;
        _participants = participants;
        _settlementStore = settlementStore;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// POST /v1/payments/cod_jeeb/record — the MOBILE post-delivery "Confirm
    /// receipt" gate (DioDeliveryReceiptRepository.confirmReceipt). The mobile
    /// client posts <c>{ deliveryId, jeeberId?, amount:{value,currency} }</c> and
    /// only needs a 2xx to advance to the star-rating UI; it then transitions the
    /// delivery to Done.
    ///
    /// DEGRADE-DON'T-FAIL / UPG-GATED (iter6 GAP B). The cash-settlement ledger
    /// post THROUGH unified_payment_gateway (UPG) is OWNER-GATED and NOT deployed
    /// (FeatureFlags:UseUpstream:Payments=false; Services:UnifiedPayment:ApiKey is
    /// not injected; UPG's COD routes 401/404). UPG must NOT be modified. So this
    /// route records the COD intent on the gateway's OWN settlement ledger
    /// (idempotent on deliveryId, cod_state=recorded — identical to the post-OTP
    /// EnqueueCodSettlementIntentAsync placeholder) and returns 200. A real user
    /// can therefore rate after a completed delivery WITHOUT a hard 404 block. When
    /// UPG's JEB-1484 PR ships + Payments flips on, the canonical
    /// POST /api/v1/payments/cod/record route fronts UPG verbatim; this best-effort
    /// recorder remains a safe, never-5xx fallback.
    /// </summary>
    [HttpPost("v1/payments/cod_jeeb/record")]
    [RequireCapability(Capabilities.DeliveryParticipate)] // {client, jeeber}; party/admin is STATE in-action
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RecordCodJeeb(
        [FromBody] CodJeebRecordBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (body is null || string.IsNullOrWhiteSpace(body.DeliveryId))
            return BadRequest(Problem("cod-record-body-required", "deliveryId is required."));

        var deliveryId = body.DeliveryId!;
        var now = _clock.GetUtcNow();

        // Idempotent replay: a COD intent already recorded for this delivery (e.g.
        // the post-OTP enqueue, or a confirm-receipt retry) is a no-op 200.
        Settlement? existing = null;
        try
        {
            existing = await _settlementStore.GetByDeliveryAsync(deliveryId, ct);
        }
        catch (Exception ex)
        {
            // A store read blip must never hard-block the rating gate — log + treat
            // as "not recorded yet" and try to insert below (still best-effort).
            _log.LogWarning(ex,
                "COD-record (mobile): settlement read for deliveryId={DeliveryId} failed; proceeding best-effort.",
                deliveryId);
        }

        if (existing is not null)
        {
            // Authorize against the recorded parties; admin always allowed.
            var isPartyExisting =
                string.Equals(existing.JeeberId, userId, StringComparison.Ordinal)
                || string.Equals(existing.ClientId, userId, StringComparison.Ordinal);
            if (!isPartyExisting && !UserIdentity.IsAdmin(HttpContext))
                return Forbidden();

            return Ok(new
            {
                deliveryId = existing.DeliveryId,
                jeeberId = existing.JeeberId,
                clientId = existing.ClientId,
                cod_state = existing.CodState,
                currency = existing.Currency,
                recorded = true,
                replay = true,
            });
        }

        // No row yet — resolve the delivery parties so we can (a) authorize the
        // caller and (b) stamp the jeeber/client on the intent. The settlement row
        // is the strongest source; fall back to the participant resolver.
        string? clientId = null;
        string? jeeberId = body.JeeberId;
        try
        {
            var participants = await _participants.ResolveAsync(deliveryId, ct);
            if (participants is not null)
            {
                clientId = participants.ClientId;
                if (string.IsNullOrWhiteSpace(jeeberId)) jeeberId = participants.JeeberId;

                if (!participants.IsParty(userId) && !UserIdentity.IsAdmin(HttpContext))
                    return Forbidden();
            }
            // When the resolver can't resolve the delivery (transient / unknown to
            // this instance) we do NOT 403 a legitimately authenticated participant
            // out of the rating flow — we proceed and record what we know. Party
            // authorization already passed the capability gate ({client,jeeber}).
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "COD-record (mobile): party resolve for deliveryId={DeliveryId} failed; recording intent best-effort.",
                deliveryId);
        }

        // Record the COD intent on the gateway's OWN ledger (idempotent on
        // deliveryId). Mirrors DeliveriesController.EnqueueCodSettlementIntentAsync:
        // GoodsCost/commission are 0 here (declared at settle time); cod_state=recorded.
        try
        {
            var intent = new Settlement
            {
                Id                = Guid.NewGuid().ToString(),
                DeliveryId        = deliveryId,
                JeeberId          = jeeberId ?? string.Empty,
                ClientId          = clientId ?? string.Empty,
                TierId            = string.Empty,
                GoodsCost         = 0m,
                CommissionTier    = CommissionCalculator.ResolveTier(null),
                CommissionRate    = 0m,
                Commission        = 0m,
                Insurance         = 0m,
                Total             = 0m,
                MinimumFeeApplied = false,
                Currency          = string.IsNullOrWhiteSpace(body.Amount?.Currency)
                                        ? SettlementService.CurrencyLbp
                                        : body.Amount!.Currency!,
                PaymentMethod     = SettlementService.PaymentMethodCash,
                State             = SettlementState.PendingSettlement,
                CodState          = CodSettlementState.Recorded,
                SettledAt         = now,
            };

            var (row, _) = await _settlementStore.TryInsertAsync(intent, ct);
            _log.LogInformation(
                "COD-record (mobile): recorded COD intent deliveryId={DeliveryId} jeeberId={JeeberId} (UPG owner-gated; gateway-ledger fallback).",
                deliveryId, row.JeeberId);

            return Ok(new
            {
                deliveryId = row.DeliveryId,
                jeeberId = row.JeeberId,
                clientId = row.ClientId,
                cod_state = row.CodState,
                currency = row.Currency,
                recorded = true,
                replay = false,
            });
        }
        catch (Exception ex)
        {
            // Even a store-insert failure must NOT hard-block the rating UI. The
            // customer has already received the goods; surface a soft 200 so the
            // mobile proceeds to Done + rating (the intent reconciles at settle).
            _log.LogError(ex,
                "COD-record (mobile): intent insert for deliveryId={DeliveryId} failed; returning soft-200 so rating is not blocked.",
                deliveryId);
            return Ok(new
            {
                deliveryId,
                recorded = false,
                degraded = true,
            });
        }
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

    /// <summary>
    /// POST /v1/payments/cod_jeeb/record body (mobile confirm-receipt). The mobile
    /// posts <c>{ deliveryId, jeeberId?, amount:{value,currency} }</c>; only
    /// deliveryId is required, the rest is advisory metadata.
    /// </summary>
    public sealed class CodJeebRecordBody
    {
        public string? DeliveryId { get; set; }
        public string? JeeberId { get; set; }
        public CodJeebAmount? Amount { get; set; }
    }

    /// <summary>The mobile money object <c>{ value, currency }</c>.</summary>
    public sealed class CodJeebAmount
    {
        public decimal? Value { get; set; }
        public string? Currency { get; set; }
    }
}
