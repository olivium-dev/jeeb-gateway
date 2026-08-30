using System.ComponentModel.DataAnnotations;

namespace JeebGateway.Partner;

/// <summary>
/// Bound configuration for the Jeeb Partner Portal wallet BFF (partner-wallet-bff).
///
/// <para><b>No secrets.</b> Every value here is non-sensitive presentation/routing metadata
/// (wallet-service transaction <c>ServiceName</c>/<c>Tag</c> labels and the ledger base
/// <c>CurrencyId</c>). The wallet-service base URL and any credentials live under the existing
/// <c>WalletServiceApi</c> section / environment, never here (olivium-secrets-hardening).</para>
///
/// <para>Bound + validated at startup via <c>AddOptions().BindConfiguration().ValidateDataAnnotations()
/// .ValidateOnStart()</c> so a mis-configured partner section fails the host loudly rather than at
/// first money move (dotnet-options-pattern skill).</para>
/// </summary>
public sealed class PartnerWalletOptions
{
    public const string SectionName = "PartnerWallet";

    /// <summary>
    /// The <c>ServiceName</c> stamped on wallet-service transactions this BFF initiates. Lets
    /// finance/ops attribute partner top-ups &amp; credits in the wallet ledger.
    /// </summary>
    [Required, MinLength(3), MaxLength(64)]
    public string ServiceName { get; init; } = "jeeb-partner-portal";

    /// <summary>Wallet-service transaction <c>Tag</c> for a partner→jeeber top-up move.</summary>
    [Required, MinLength(3), MaxLength(64)]
    public string TopupTag { get; init; } = "partner-topup";

    /// <summary>Wallet-service transaction <c>Tag</c> for an admin cash-credit into a partner wallet.</summary>
    [Required, MinLength(3), MaxLength(64)]
    public string CreditTag { get; init; } = "partner-cash-credit";

    /// <summary>
    /// Whether wallet-service may add configured fee legs to partner top-ups. This stays false
    /// until the partner cash-in flow also funds the separate base-currency fee-source wallet;
    /// enabling fees before then would make an otherwise funded demo top-up fail at execution.
    /// </summary>
    public bool ApplyConfiguredTopupFees { get; init; } = false;

    /// <summary>
    /// The wallet-service currency id the partner wallet operates in. Used only to pick the
    /// holder's matching wallet among (possibly) several; NOT a money computation.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int CurrencyId { get; init; } = 1;

    /// <summary>
    /// Upper bound (inclusive) the gateway rejects a single top-up/credit ABOVE with a 400 before
    /// any wallet-service call — a cheap guardrail against fat-finger amounts, NOT a fee rule. The
    /// authoritative limits (balance, fees, BR caps) remain wallet-service's.
    /// </summary>
    [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
    public decimal MaxTransferAmount { get; init; } = 100_000m;

    /// <summary>
    /// PP-7 OTP step-up threshold (config key <c>PartnerWallet__OtpStepUpThreshold</c>). A
    /// partner→jeeber top-up whose gross Amount is STRICTLY ABOVE this value requires a one-time
    /// step-up code (challenge → verify) before it executes; an amount AT OR BELOW it flows unchanged
    /// (backward compatible — an existing client that never sends the OTP fields is unaffected).
    /// Default 50 is an owner-gated assumption (surfaced, not decided). Amounts compared here are the
    /// transfer's gross Amount in the partner wallet currency. Same <see cref="RangeAttribute"/>
    /// fail-fast style as <see cref="MaxTransferAmount"/> so a mis-configured threshold fails the host
    /// loudly at startup rather than at first high-value move (dotnet-options-pattern skill).
    /// </summary>
    [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
    public decimal OtpStepUpThreshold { get; init; } = 50m;

    // ── BOPLA / target-type guard (OWASP API3) ──────────────────────────────────────────────
    //
    // A partner's top-up destination and an admin credit's target are resolved from a caller-supplied
    // holder GUID. Without a type check a partner could direct their own money into ANY provisioned
    // wallet (another partner, a customer, an admin), and the route/DTO name "jeeber" would misstate
    // the enforced constraint. When ENABLED, a move is rejected unless the destination/source holder's
    // wallet-service HolderType MUST be in the configured set for its role. This
    // guard is mandatory and fail-closed; money routes cannot be configured back
    // to accepting an arbitrary provisioned holder GUID.

    /// <summary>Comma-separated authoritative holder types accepted as Jeeber top-up destinations.</summary>
    [Required, MinLength(1), MaxLength(128)]
    public string JeeberHolderTypes { get; init; } = "jeeber";

    /// <summary>Comma-separated authoritative holder types accepted as partner cash-credit targets.</summary>
    [Required, MinLength(1), MaxLength(128)]
    public string PartnerHolderTypes { get; init; } = "partner";

    // Holder spendability is NOT configured here: it stays with the one ratified predicate,
    // JeebWallet.SpendableWalletTypes (R-M1/G-01). Only SYSTEM identity needs a positive allow-list.

    /// <summary>Comma-separated <c>Wallet.Type</c> tokens identifying the SYSTEM wallet (case-insensitive).</summary>
    /// <remarks>Defaults are wallet-service <c>DataFeed</c>'s seeded types; also reserved — no holder may spend one.</remarks>
    public string SystemWalletTypes { get; init; } = "__SYSTEM__,__SYSTEM__PRIMARY__";
}
