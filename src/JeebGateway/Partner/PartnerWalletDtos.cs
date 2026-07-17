using System;
using System.ComponentModel.DataAnnotations;

namespace JeebGateway.Partner;

// ── Requests ────────────────────────────────────────────────────────────────────────────
// DataAnnotations drive the [ApiController] automatic 400 → application/problem+json
// (RFC 7807 ValidationProblemDetails) with NO per-action validation code (dotnet-problem-details
// skill). All amounts are the CALLER's; the gateway never derives a monetary value from them.

/// <summary>Preview the fees for a partner→jeeber top-up (POST v1/partner/wallet/transfers/predict).</summary>
public sealed class PartnerTopupPredictRequest
{
    /// <summary>The destination jeeber's wallet-holder id (their user GUID).</summary>
    [Required]
    public Guid JeeberId { get; init; }

    /// <summary>The gross amount the partner intends to move into the jeeber wallet.</summary>
    [Range(0.01, double.MaxValue, ErrorMessage = "amount must be greater than 0.")]
    public double Amount { get; init; }
}

/// <summary>Execute a partner→jeeber top-up (POST v1/partner/wallet/transfers).</summary>
public sealed class PartnerTopupExecuteRequest
{
    /// <summary>The destination jeeber's wallet-holder id (their user GUID).</summary>
    [Required]
    public Guid JeeberId { get; init; }

    /// <summary>The gross amount to move from the partner wallet into the jeeber wallet.</summary>
    [Range(0.01, double.MaxValue, ErrorMessage = "amount must be greater than 0.")]
    public double Amount { get; init; }

    /// <summary>
    /// Client-supplied idempotency key so a retried confirm does not double-move money. Echoed to
    /// wallet-service as the transaction <c>Notes</c>; REQUIRED on a money-mutating confirm.
    /// </summary>
    [Required, MinLength(8), MaxLength(128)]
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>Optional free-text note the partner attaches (receipt reference, etc.).</summary>
    [MaxLength(280)]
    public string? Note { get; init; }
}

/// <summary>
/// Admin records offline cash a partner handed over and credits the partner wallet
/// (POST v1/admin/partners/{partnerId}/wallet/credits). Evidence note is MANDATORY (audit trail).
/// </summary>
public sealed class PartnerCashCreditRequest
{
    /// <summary>The cash amount received from the partner, to credit into the partner wallet.</summary>
    [Range(0.01, double.MaxValue, ErrorMessage = "amount must be greater than 0.")]
    public double Amount { get; init; }

    /// <summary>
    /// MANDATORY evidence note (receipt no. / handover reference / who received the cash). Recorded
    /// on the wallet-service transaction and in the gateway audit log — an admin money-in event may
    /// never be un-evidenced.
    /// </summary>
    [Required, MinLength(4), MaxLength(280)]
    public string EvidenceNote { get; init; } = string.Empty;

    /// <summary>
    /// Client-supplied idempotency key so a double-submitted / retried cash-credit does not
    /// DOUBLE-CREATE money for a single physical cash handover. Enforced as a real dedup key
    /// (gateway-side, before the wallet-service saga) — REQUIRED on this money-creation path.
    /// </summary>
    [Required, MinLength(8), MaxLength(128)]
    public string IdempotencyKey { get; init; } = string.Empty;
}

// ── Responses ───────────────────────────────────────────────────────────────────────────

/// <summary>Partner wallet balance/summary, projected from the generic holder-wallets read.</summary>
public sealed class PartnerWalletBalanceResponse
{
    public Guid PartnerId { get; init; }
    public string? PartnerName { get; init; }
    public double Balance { get; init; }
    public int CurrencyId { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Fee preview for a partner→jeeber top-up (verbatim projection of wallet-service Predict).</summary>
public sealed class PartnerTopupPreviewResponse
{
    public Guid JeeberId { get; init; }
    public double GrossAmount { get; init; }
    /// <summary>Fees as computed by wallet-service (NOT the gateway). Flows to the system wallet.</summary>
    public double Fees { get; init; }
    public double NetToJeeber { get; init; }
    public string? Summary { get; init; }
}

/// <summary>Result of an executed partner→jeeber top-up or admin cash credit.</summary>
public sealed class PartnerWalletMoveResponse
{
    public Guid TransactionId { get; init; }
    public double Amount { get; init; }
    public double Fees { get; init; }
    public string Status { get; init; } = "executed";
}

/// <summary>Whether a jeeber is a valid top-up destination (has a provisioned wallet).</summary>
public sealed class PartnerJeeberTargetResponse
{
    public Guid JeeberId { get; init; }
    public bool HasWallet { get; init; }
    public string? JeeberName { get; init; }
}
