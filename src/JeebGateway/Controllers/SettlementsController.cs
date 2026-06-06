using JeebGateway.Auth.Capabilities;
using JeebGateway.Financials;
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
    private readonly TimeProvider _clock;

    public SettlementsController(
        ISettlementService settlements,
        ISettlementStore store,
        TimeProvider clock)
    {
        _settlements = settlements;
        _store = store;
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
