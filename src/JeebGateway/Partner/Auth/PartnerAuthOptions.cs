using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace JeebGateway.Partner.Auth;

/// <summary>
/// Bound configuration for the Jeeb Partner Portal login front door (PP-1, partner-wallet-bff).
///
/// <para><b>Admin-provisioned, no self-registration (owner decision).</b> A partner (cash shop /
/// agent) never registers itself; an operator provisions each partner as a roster row here. Each row
/// binds a login handle to the partner's <b>user-management userId</b> (the wallet holder id — owner
/// decision <c>holderId == user-management userId</c>) and a <b>salted-free SHA-256 hash of the
/// partner secret</b> — never the plaintext secret. The gateway verifies a presented secret by
/// hashing it and constant-time-comparing, so the committed config carries no recoverable
/// credential (olivium-secrets-hardening: no <c>P@ssw0rd</c>-class literal, no plaintext secret).</para>
///
/// <para><b>Why a gateway-own store and not a user-management call.</b> Customer login is
/// user-management-backed, but user-management exposes no per-partner credential-verification surface
/// to the gateway (its <c>UserIdLoginAsync</c> is the super-admin passcode gate, not a partner
/// credential), and adding one is a user-management change out of scope for this gateway-only ticket.
/// The gateway therefore owns the partner credential <i>verification</i> while user-management remains
/// the identity authority for the <c>holderId</c> itself (the roster maps login → the UM userId). This
/// is the task's documented fallback ("otherwise the gateway's own store").</para>
///
/// <para>Bound + validated at startup (<c>AddOptions().Bind().ValidateDataAnnotations()
/// .ValidateOnStart()</c>) so a malformed roster fails the host loudly rather than at first login
/// (dotnet-options-pattern). An <b>empty</b> roster is valid (dev/CI seed the store at runtime through
/// the <c>[DevOnly]</c> hook); each PRESENT row must be complete.</para>
/// </summary>
public sealed class PartnerAuthOptions
{
    public const string SectionName = "PartnerAuth";

    /// <summary>
    /// The admin-provisioned partner roster. May be empty (dev/CI seed at runtime). Each PRESENT row
    /// is validated at startup by the custom option validator wired in
    /// <c>PartnerWalletExtensions.AddPartnerWallet</c> (row-level DataAnnotations over the list).
    /// </summary>
    public List<PartnerCredentialRow> Credentials { get; init; } = new();
}

/// <summary>
/// A single admin-provisioned partner credential. Non-sensitive routing metadata plus a one-way hash
/// of the secret — the plaintext secret is never stored, transmitted in config, or logged.
/// </summary>
public sealed class PartnerCredentialRow
{
    /// <summary>The login handle the partner types (email or partner id — matches the portal's "Email or partner ID" field).</summary>
    [Required, MinLength(3), MaxLength(256)]
    public string Login { get; init; } = string.Empty;

    /// <summary>
    /// The partner's user-management userId, which IS the wallet holder id (owner decision
    /// <c>holderId == user-management userId</c>). Embedded as the minted token's <c>sub</c> so every
    /// downstream partner-wallet call resolves the same holder.
    /// </summary>
    [Required]
    public string HolderId { get; init; } = string.Empty;

    /// <summary>Human display name surfaced back to the portal after login (profile basics). Non-secret.</summary>
    [Required, MinLength(1), MaxLength(256)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Lowercase/uppercase-insensitive HEX of <c>SHA-256(secret)</c> (64 hex chars). Admin computes
    /// this when provisioning (e.g. <c>printf %s "$secret" | sha256sum</c>) so the plaintext never
    /// touches config. The gateway hashes the presented secret and constant-time-compares.
    /// </summary>
    [Required, RegularExpression("^[0-9a-fA-F]{64}$", ErrorMessage = "SecretSha256 must be 64 hex chars (SHA-256).")]
    public string SecretSha256 { get; init; } = string.Empty;
}
