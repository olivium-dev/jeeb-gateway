using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
using JeebGateway.Tracking;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Cash settlement and receipt API (T-backend-016 / JEEB-34).
///
/// Pair of Jeeb-owned endpoints that close out a successful delivery:
/// <list type="bullet">
///   <item>POST /deliveries/{id}/settle — the assigned Jeeber records the
///         cash they collected. The gateway computes the fee breakdown
///         (commission % + 2% insurance, min 1000 LBP) and posts a single
///         ledger entry to wallet-service.</item>
///   <item>GET /deliveries/{id}/receipt — returns the persisted settlement
///         as a render-ready receipt. The state machine advances from
///         <c>settled</c> to <c>receipt_generated</c> on the first read.</item>
/// </list>
///
/// All Jeeb business logic — fee policy, tier-to-commission mapping,
/// authorization — lives behind <see cref="ISettlementService"/>. The
/// controller is intentionally a thin HTTP adapter so the boundary with
/// wallet-service stays generic.
/// </summary>
[ApiController]
[Route("deliveries")]
public class SettlementsController : ControllerBase
{
    private readonly ISettlementService _settlements;
    private readonly ISettlementStore _store;
    private readonly IDeliveryParticipantResolver _participants;
    private readonly TimeProvider _clock;

    public SettlementsController(
        ISettlementService settlements,
        ISettlementStore store,
        IDeliveryParticipantResolver participants,
        TimeProvider clock)
    {
        _settlements = settlements;
        _store = store;
        _participants = participants;
        _clock = clock;
    }

    [HttpPost("{deliveryId}/settle")]
    // ADR-005 L2 §E: Settle is a delivery-SM/dual-party action — coarse cap delivery.participate
    // {client, jeeber}. "Only the assigned Jeeber can settle" (callerIsJeeber / NotAuthorized)
    // is STATE/party-on-delivery and stays in the service — never an L2 policy.
    [RequireCapability(Capabilities.DeliveryParticipate)]
    [ProducesResponseType(typeof(SettleDeliveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Settle(
        string deliveryId,
        [FromBody] SettleDeliveryRequest? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/settlement-body-required",
            });
        }

        var callerIsJeeber = UserIdentity.HasRole(HttpContext, Roles.Jeeber);
        var result = await _settlements.SettleAsync(deliveryId, userId, callerIsJeeber, body, ct);

        switch (result.Outcome)
        {
            case SettlementOutcome.DeliveryNotFound:
                return NotFound();

            case SettlementOutcome.NotAuthorized:
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "Only the assigned Jeeber can settle this delivery.",
                    Status = StatusCodes.Status403Forbidden,
                    Type = "https://jeeb.dev/errors/settlement-not-authorized",
                });

            case SettlementOutcome.NotDelivered:
                return Conflict(new ProblemDetails
                {
                    Title = "Delivery is not in a settle-able state.",
                    Detail = result.Reason,
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/settlement-not-delivered",
                });

            case SettlementOutcome.InvalidAmount:
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid goods cost.",
                    Detail = result.Reason,
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/settlement-invalid-amount",
                });

            case SettlementOutcome.InvalidPaymentMethod:
                return BadRequest(new ProblemDetails
                {
                    Title = "Unsupported payment method.",
                    Detail = result.Reason,
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/settlement-invalid-payment-method",
                });

            case SettlementOutcome.AlreadySettled:
            case SettlementOutcome.Settled:
                return Ok(ToResponse(result.Settlement!));

            default:
                return Problem(
                    title: "Unhandled settlement outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet("{deliveryId}/receipt")]
    // ADR-005 L2 §E: the receipt is read by the delivery PARTIES — coarse cap
    // delivery.participate {client, jeeber}. The exact "are you a party / admin override"
    // (isClient || isJeeber || isAdmin) is STATE and stays in the action body. (Admin's
    // read-any path is the downstream STATE branch, not an L2 claim — see triage note.)
    [RequireCapability(Capabilities.DeliveryParticipate)]
    [ProducesResponseType(typeof(ReceiptResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Receipt(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        var settlement = await _settlements.GetByDeliveryAsync(deliveryId, ct);
        if (settlement is null)
        {
            return NotFound();
        }

        var isClient = string.Equals(settlement.ClientId, userId, StringComparison.Ordinal);
        var isJeeber = string.Equals(settlement.JeeberId, userId, StringComparison.Ordinal);
        var isAdmin = UserIdentity.IsAdmin(HttpContext);
        if (!isClient && !isJeeber && !isAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You are not a party to this delivery.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/receipt-not-a-party",
            });
        }

        // Advance state on first read so admins can tell apart "Jeeber
        // settled but no one looked at it" from "client picked up their
        // receipt". The store method is idempotent — repeat reads keep the
        // original timestamp.
        var stamped = await _store.MarkReceiptGeneratedAsync(settlement.Id, _clock.GetUtcNow(), ct) ?? settlement;

        var receipt = ReceiptGenerator.Generate(stamped, _clock.GetUtcNow());
        return Ok(receipt);
    }

    /// <summary>
    /// GET /v1/deliveries/{id}/settlement (S09 H8 / JEB-54).
    ///
    /// The settlement-intent READ. Returns the open commission intent for a
    /// delivery (idempotent on deliveryId — a repeat read never double-creates
    /// and the duplicate verify after Done does NOT double-settle, A7/N11). S09
    /// asserts only the enqueue + window-open; the fee math + ledger posting are
    /// the S10 concern behind POST /deliveries/{id}/settle.
    ///
    /// <para>
    /// Authorization mirrors the receipt read: the delivery parties (Client /
    /// Jeeber) + admin may read. Party membership is resolved via the
    /// delivery-service-backed <see cref="IDeliveryParticipantResolver"/> — the
    /// authority for who is bound to the delivery — so a non-party gets 403 and
    /// an unknown delivery gets 404. No cross-service DB read: the gateway
    /// composes the delivery-service party verdict with its own settlement store.
    /// </para>
    /// </summary>
    [HttpGet("/v1/deliveries/{deliveryId}/settlement")]
    // ADR-005 L2 §E: the settlement intent is read by the delivery PARTIES — coarse
    // cap delivery.participate {client, jeeber}. Exact party/admin membership is STATE,
    // resolved in-action against delivery-service (same shape as the receipt read).
    [RequireCapability(Capabilities.DeliveryParticipate)]
    [ProducesResponseType(typeof(SettlementIntentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSettlementIntent(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        // A persisted settlement (the Jeeber already recorded the cash) is the
        // authoritative party + state source — read it first so a settled
        // delivery reflects the real row verbatim and the read is idempotent.
        var settlement = await _settlements.GetByDeliveryAsync(deliveryId, ct);
        if (settlement is not null)
        {
            var isClient = string.Equals(settlement.ClientId, userId, StringComparison.Ordinal);
            var isJeeber = string.Equals(settlement.JeeberId, userId, StringComparison.Ordinal);
            if (!isClient && !isJeeber && !UserIdentity.IsAdmin(HttpContext))
            {
                return Forbidden();
            }

            return Ok(new SettlementIntentResponse
            {
                DeliveryId = settlement.DeliveryId,
                State = settlement.State,
                Created = true,
                SettlementId = settlement.Id,
                Total = settlement.Total,
                Currency = settlement.Currency,
            });
        }

        // No persisted settlement yet — resolve the delivery to authorize the
        // caller and to decide whether the commission window has opened. The
        // intent is "open" (pending_settlement) once the delivery reaches the
        // settle-able terminal state (Done / delivered); before that there is
        // no intent to read.
        var participants = await _participants.ResolveAsync(deliveryId, ct);
        if (participants is null)
        {
            return NotFound();
        }

        if (!participants.IsParty(userId) && !UserIdentity.IsAdmin(HttpContext))
        {
            return Forbidden();
        }

        var windowOpen = IsSettleable(participants.Status);
        return Ok(new SettlementIntentResponse
        {
            DeliveryId = participants.DeliveryId,
            State = windowOpen ? SettlementState.PendingSettlement : "not_ready",
            Created = windowOpen,
        });
    }

    /// <summary>
    /// True when the delivery has reached the handover-complete terminal state
    /// against which a settlement may be recorded — the canonical <c>Done</c> or
    /// the legacy mirror <c>delivered</c>/<c>rated</c>. The commission intent
    /// window opens here (H8 enqueue assertion).
    /// </summary>
    private static bool IsSettleable(string status) =>
        string.Equals(status, "Done", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "delivered", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "rated", StringComparison.OrdinalIgnoreCase);

    private IActionResult Forbidden() => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
    {
        Title = "You are not a party to this delivery.",
        Status = StatusCodes.Status403Forbidden,
        Type = "https://jeeb.dev/errors/settlement-not-a-party",
    });

    private static SettleDeliveryResponse ToResponse(Settlement s) => new()
    {
        SettlementId = s.Id,
        DeliveryId = s.DeliveryId,
        State = s.State,
        GoodsCost = s.GoodsCost,
        CommissionTier = s.CommissionTier.ToString(),
        CommissionRate = s.CommissionRate,
        Commission = s.Commission,
        Insurance = s.Insurance,
        Total = s.Total,
        MinimumFeeApplied = s.MinimumFeeApplied,
        Currency = s.Currency,
        PaymentMethod = s.PaymentMethod,
        SettledAt = s.SettledAt,
        LedgerEntryId = s.LedgerEntryId,
    };
}
