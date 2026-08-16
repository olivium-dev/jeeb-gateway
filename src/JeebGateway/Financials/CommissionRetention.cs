using JeebGateway.Observability;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials;

/// <summary>
/// O1: an accepted delivery is charged its platform fee at ACCEPT. When such a delivery is later
/// cancelled the fee stays taken — <b>no refund is implemented and none is invented here</b>, because
/// the owner has not ruled on refunds. This makes the retained amount explicit and countable so the
/// decision is priced from data rather than guessed.
/// </summary>
/// <remarks>Shared by every cancel seam because cancellation never converged on one, unlike accept.</remarks>
public static class CommissionRetention
{
    public static void Observe(
        ILogger logger, string? deliveryId, string? jeeberId, decimal? acceptedFee, string cancelledBy)
    {
        if (string.IsNullOrWhiteSpace(jeeberId)) return;

        var fee = acceptedFee ?? 0m;
        if (fee <= 0m) return;

        var retained = WalletGuardContract.RequiredCommission(fee);
        BusinessOutcomeTelemetry.CommissionRetainedOnCancel.Add(1);
        logger.LogInformation(
            "commission.cancel.retained deliveryId={DeliveryId} jeeberId={JeeberId} cancelledBy={By} "
            + "acceptedFee={Fee} retained={Retained} — the accept-time platform fee is NOT refunded "
            + "(no refund policy exists; the O1 refund question is open with the owner).",
            deliveryId, jeeberId, cancelledBy, fee, retained);
    }
}
